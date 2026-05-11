using GrokPY.Core.Models;
using GrokPY.Services.Chrome;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace GrokPY.Services.Api;

/// <summary>
/// Upload ảnh lên Sora API (OpenAI Sora)
/// Tương đương SORA_API_UPLOAD_IMAGE.py trong Python gốc
/// Dùng StealthChrome vì cần session đăng nhập Sora
/// </summary>
public class SoraUploadService
{
    private readonly ILogger<SoraUploadService> _logger;
    private readonly SettingsManager _settings;
    private readonly ChromeProcessManager _chromeManager;

    private const string SoraBaseUrl   = "https://sora.com";
    private const string UploadApiUrl  = "https://sora.com/api/upload";
    private const string GenerateUrl   = "https://sora.com/api/generations";

    public SoraUploadService(
        ILogger<SoraUploadService> logger,
        SettingsManager settings,
        ChromeProcessManager chromeManager)
    {
        _logger        = logger;
        _settings      = settings;
        _chromeManager = chromeManager;
    }

    // ─── Public API ───────────────────────────────────────────

    /// <summary>
    /// Upload ảnh lên Sora và tạo video từ ảnh đó
    /// </summary>
    /// <param name="imagePath">Đường dẫn ảnh</param>
    /// <param name="prompt">Mô tả video</param>
    /// <param name="duration">Độ dài video (giây)</param>
    /// <param name="profileDir">Chrome profile có session Sora</param>
    /// <param name="outputDir">Thư mục lưu video</param>
    /// <param name="onProgress">Callback tiến độ</param>
    public async Task<VideoGenResult> UploadAndGenerateAsync(
        string imagePath,
        string prompt      = "",
        int    duration    = 5,
        string? profileDir = null,
        string? outputDir  = null,
        Action<string>? onProgress = null)
    {
        if (!File.Exists(imagePath))
            return VideoGenResult.Fail($"File ảnh không tồn tại: {imagePath}");

        var chromePath = _chromeManager.FindChromePath();
        if (chromePath == null)
            return VideoGenResult.Fail("Không tìm thấy Chrome.");

        // Dùng profile riêng cho Sora
        profileDir ??= Path.Combine(_settings.DataDir, "chrome_sora", "PROFILE_1");
        Directory.CreateDirectory(profileDir);

        outputDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "GrokPY", "Sora", DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(outputDir);

        var chrome = new StealthChrome(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StealthChrome>.Instance);

        try
        {
            onProgress?.Invoke("🚀 Đang mở Chrome cho Sora...");
            await chrome.LaunchAsync(chromePath, profileDir, SoraBaseUrl, headless: false);
            await Task.Delay(3000);

            // Kiểm tra đã đăng nhập Sora chưa
            var isLoggedIn = await CheckSoraLoginAsync(chrome);
            if (!isLoggedIn)
            {
                onProgress?.Invoke("⚠️ Chưa đăng nhập Sora!");
                _logger.LogWarning("Chưa đăng nhập Sora — cần đăng nhập thủ công");
                return VideoGenResult.Fail(
                    "Chưa đăng nhập Sora. Mở Chrome, vào sora.com và đăng nhập trước.");
            }

            // Bước 1: Upload ảnh
            onProgress?.Invoke("📤 Đang upload ảnh lên Sora...");
            var mediaId = await UploadImageAsync(chrome, imagePath);
            if (string.IsNullOrEmpty(mediaId))
                return VideoGenResult.Fail("Upload ảnh thất bại.");

            _logger.LogInformation("Sora mediaId: {Id}", mediaId);

            // Bước 2: Tạo generation với ảnh đã upload
            onProgress?.Invoke("🎬 Đang tạo video từ ảnh...");
            var generationId = await CreateGenerationAsync(
                chrome, mediaId, prompt, duration);
            if (string.IsNullOrEmpty(generationId))
                return VideoGenResult.Fail("Tạo generation thất bại.");

            // Bước 3: Poll chờ video xong
            onProgress?.Invoke("⏳ Đang chờ Sora xử lý...");
            var videoUrl = await PollGenerationAsync(chrome, generationId, onProgress);
            if (string.IsNullOrEmpty(videoUrl))
                return VideoGenResult.Fail("Timeout chờ Sora.");

            // Bước 4: Download
            onProgress?.Invoke("⬇️ Đang tải video Sora về...");
            var saved = await DownloadAsync(videoUrl, outputDir);
            if (saved == null) return VideoGenResult.Fail("Download thất bại.");

            onProgress?.Invoke("✅ Sora tạo video thành công!");
            return VideoGenResult.Success(new List<string> { saved });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi Sora upload");
            return VideoGenResult.Fail($"Lỗi: {ex.Message}");
        }
        finally
        {
            await chrome.DisposeAsync();
        }
    }

    // ─── Steps ────────────────────────────────────────────────

    /// <summary>
    /// Kiểm tra đã đăng nhập Sora chưa
    /// </summary>
    private async Task<bool> CheckSoraLoginAsync(StealthChrome chrome)
    {
        try
        {
            var url = await chrome.EvaluateAsync<string>("window.location.href");
            // Nếu redirect về login page → chưa đăng nhập
            if (url != null && url.Contains("/login")) return false;

            // Kiểm tra có user session không
            var hasUser = await chrome.EvaluateAsync<bool>(@"
                document.querySelector('[data-testid=""user-menu""]') !== null ||
                document.querySelector('[aria-label=""Account""]') !== null ||
                document.cookie.includes('session')
            ");
            return hasUser;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Upload ảnh lên Sora — dùng JS fetch trong Chrome session
    /// </summary>
    private async Task<string?> UploadImageAsync(StealthChrome chrome, string imagePath)
    {
        try
        {
            var imageBytes  = await File.ReadAllBytesAsync(imagePath);
            var imageBase64 = Convert.ToBase64String(imageBytes);
            var mimeType    = GetMimeType(imagePath);
            var fileName    = Path.GetFileName(imagePath);

            // Upload qua FormData trong Chrome
            var response = await chrome.EvaluateAsync<string>($@"
                async () => {{
                    try {{
                        // Chuyển base64 → Blob
                        const b64 = '{imageBase64}';
                        const byteChars = atob(b64);
                        const byteNums = new Array(byteChars.length);
                        for (let i = 0; i < byteChars.length; i++)
                            byteNums[i] = byteChars.charCodeAt(i);
                        const blob = new Blob([new Uint8Array(byteNums)],
                            {{ type: '{mimeType}' }});

                        // Tạo FormData
                        const form = new FormData();
                        form.append('file', blob, '{fileName}');

                        const res = await fetch('{UploadApiUrl}', {{
                            method: 'POST',
                            body: form,
                            credentials: 'include'
                        }});

                        if (!res.ok) return JSON.stringify({{ error: res.status }});
                        return await res.text();
                    }} catch(e) {{
                        return JSON.stringify({{ error: String(e) }});
                    }}
                }}
            ");

            if (string.IsNullOrEmpty(response)) return null;

            var node = JsonNode.Parse(response);
            return node?["id"]?.GetValue<string>()
                ?? node?["mediaId"]?.GetValue<string>()
                ?? node?["asset_id"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upload ảnh Sora lỗi");
            return null;
        }
    }

    /// <summary>
    /// Tạo generation job trên Sora
    /// </summary>
    private async Task<string?> CreateGenerationAsync(
        StealthChrome chrome,
        string mediaId,
        string prompt,
        int duration)
    {
        var payload = new
        {
            prompt       = prompt,
            duration     = duration,
            aspect_ratio = "9:16",
            media_ids    = new[] { mediaId }
        };

        var response = await chrome.FetchAsync(GenerateUrl, "POST", payload);
        if (response == null) return null;

        try
        {
            var node = JsonNode.Parse(response);
            return node?["id"]?.GetValue<string>()
                ?? node?["generation_id"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Poll chờ Sora xử lý xong
    /// </summary>
    private async Task<string?> PollGenerationAsync(
        StealthChrome chrome,
        string generationId,
        Action<string>? onProgress)
    {
        var statusUrl = $"{GenerateUrl}/{generationId}";
        var deadline  = DateTime.UtcNow.AddMinutes(10);
        var attempt   = 0;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(5000);
            attempt++;
            var elapsed = TimeSpan.FromSeconds(attempt * 5);
            onProgress?.Invoke($"⏳ Sora đang xử lý... {elapsed:mm\\:ss}");

            try
            {
                var response = await chrome.FetchAsync(statusUrl, "GET");
                if (response == null) continue;

                var node   = JsonNode.Parse(response);
                var status = node?["status"]?.GetValue<string>()
                          ?? node?["state"]?.GetValue<string>()
                          ?? string.Empty;

                if (status is "failed" or "error") return null;

                if (status is "completed" or "succeeded" or "ready")
                {
                    return node?["video_url"]?.GetValue<string>()
                        ?? node?["url"]?.GetValue<string>()
                        ?? node?["download_url"]?.GetValue<string>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poll Sora lần {A}", attempt);
            }
        }
        return null;
    }

    // ─── Helpers ──────────────────────────────────────────────

    private async Task<string?> DownloadAsync(string url, string outputDir)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient();
            var bytes      = await http.GetByteArrayAsync(url);
            var filePath   = Path.Combine(outputDir,
                $"sora_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            await File.WriteAllBytesAsync(filePath, bytes);
            _logger.LogInformation("Đã lưu Sora: {P}", filePath);
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download Sora lỗi");
            return null;
        }
    }

    private static string GetMimeType(string path) =>
        Path.GetExtension(path).ToLower() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp"           => "image/webp",
            _                 => "image/png"
        };
}
