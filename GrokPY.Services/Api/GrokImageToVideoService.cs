using GrokPY.Core.Models;
using GrokPY.Services.Chrome;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace GrokPY.Services.Api;

/// <summary>
/// Tạo video từ ảnh dùng Grok (grok.com) qua Chrome session
/// Tương đương grok_api_image_to_video.py trong Python gốc
/// Khác T2V: payload có thêm trường imageUrl/imageBase64
/// </summary>
public class GrokImageToVideoService
{
    private readonly ILogger<GrokImageToVideoService> _logger;
    private readonly SettingsManager _settings;
    private readonly ChromeProcessManager _chromeManager;

    private const string CreatePostUrl = "https://grok.com/rest/media/post/create";
    private const string ChatUrl       = "https://grok.com/rest/app-chat/conversations/new";
    private const string UpscaleUrl    = "https://grok.com/rest/media/video/upscale";
    private const string GrokBaseUrl   = "https://grok.com";

    private const int MaxWaitMs      = 600_000;
    private const int PollIntervalMs = 3_000;

    public GrokImageToVideoService(
        ILogger<GrokImageToVideoService> logger,
        SettingsManager settings,
        ChromeProcessManager chromeManager)
    {
        _logger        = logger;
        _settings      = settings;
        _chromeManager = chromeManager;
    }

    // ─── Public API ───────────────────────────────────────────

    /// <summary>
    /// Tạo video từ ảnh qua Grok
    /// </summary>
    /// <param name="imagePath">Đường dẫn ảnh đầu vào</param>
    /// <param name="prompt">Mô tả chuyển động</param>
    /// <param name="aspectRatio">VideoAspectRatio.*</param>
    /// <param name="upscale">Có upscale video không</param>
    /// <param name="outputDir">Thư mục lưu</param>
    /// <param name="profileDir">Chrome profile Grok</param>
    /// <param name="onProgress">Callback tiến độ</param>
    public async Task<VideoGenResult> GenerateAsync(
        string imagePath,
        string prompt          = "",
        string aspectRatio     = VideoAspectRatio.Portrait,
        bool   upscale         = true,
        string? outputDir      = null,
        string? profileDir     = null,
        Action<string>? onProgress = null)
    {
        if (!File.Exists(imagePath))
            return VideoGenResult.Fail($"File ảnh không tồn tại: {imagePath}");

        var chromePath = _chromeManager.FindChromePath();
        if (chromePath == null)
            return VideoGenResult.Fail("Không tìm thấy Chrome.");

        profileDir ??= _settings.GetGrokChromeProfileDir();
        outputDir  ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "GrokPY", "GrokVideos", DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(outputDir);

        var chrome = new StealthChrome(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StealthChrome>.Instance);

        try
        {
            onProgress?.Invoke("🚀 Đang mở Chrome...");
            await chrome.LaunchAsync(chromePath, profileDir, GrokBaseUrl, headless: false);
            await Task.Delay(2000);

            // Đọc ảnh → base64 để upload
            onProgress?.Invoke("🖼️ Đang chuẩn bị ảnh...");
            var imageBytes  = await File.ReadAllBytesAsync(imagePath);
            var imageBase64 = Convert.ToBase64String(imageBytes);
            var mimeType    = GetMimeType(imagePath);
            var dataUri     = $"data:{mimeType};base64,{imageBase64}";

            // Bước 1: Tạo media post với ảnh
            onProgress?.Invoke("📤 Đang tạo media post...");
            var postId = await CreateMediaPostWithImageAsync(
                chrome, prompt, aspectRatio, dataUri);
            if (string.IsNullOrEmpty(postId))
                return VideoGenResult.Fail("Không tạo được media post.");

            _logger.LogInformation("PostId: {Id}", postId);

            // Bước 2: Start generation
            onProgress?.Invoke("🎬 Đang bắt đầu generate...");
            await StartGenerationAsync(chrome, postId, prompt);

            // Bước 3: Poll
            onProgress?.Invoke("⏳ Chờ Grok xử lý...");
            var videoUrl = await PollForVideoAsync(chrome, postId, onProgress);
            if (string.IsNullOrEmpty(videoUrl))
                return VideoGenResult.Fail("Timeout chờ video.");

            // Bước 4: Upscale
            if (upscale)
            {
                onProgress?.Invoke("⬆️ Đang upscale...");
                var up = await UpscaleAsync(chrome, postId, videoUrl);
                if (!string.IsNullOrEmpty(up)) videoUrl = up;
            }

            // Bước 5: Download
            onProgress?.Invoke("⬇️ Đang tải video...");
            var saved = await DownloadAsync(videoUrl, outputDir);
            if (saved == null) return VideoGenResult.Fail("Download thất bại.");

            onProgress?.Invoke("✅ Tạo video Grok I2V thành công!");
            return VideoGenResult.Success(new List<string> { saved });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi Grok I2V");
            return VideoGenResult.Fail($"Lỗi: {ex.Message}");
        }
        finally
        {
            await chrome.DisposeAsync();
        }
    }

    // ─── Steps ────────────────────────────────────────────────

    private async Task<string?> CreateMediaPostWithImageAsync(
        StealthChrome chrome,
        string prompt,
        string aspectRatio,
        string dataUri)
    {
        // Payload có thêm referenceImage so với T2V
        var payload = new
        {
            mediaType      = "MEDIA_POST_TYPE_VIDEO",
            prompt         = prompt,
            aspectRatio    = aspectRatio,
            videoModel     = "grok-3",
            referenceImage = dataUri    // ảnh đầu vào
        };

        var response = await chrome.FetchAsync(CreatePostUrl, "POST", payload);
        if (response == null) return null;

        try
        {
            var node = JsonNode.Parse(response);
            return node?["postId"]?.GetValue<string>()
                ?? node?["id"]?.GetValue<string>()
                ?? node?["mediaPost"]?["id"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private async Task StartGenerationAsync(
        StealthChrome chrome, string postId, string prompt)
    {
        var payload = new
        {
            message = new
            {
                content      = prompt.Length > 0 ? prompt : "Animate this image",
                mediaPostIds = new[] { postId }
            },
            modelName = "grok-3",
            temporary = true,
            videoGen  = true
        };
        await chrome.FetchAsync(ChatUrl, "POST", payload);
    }

    private async Task<string?> PollForVideoAsync(
        StealthChrome chrome, string postId, Action<string>? onProgress)
    {
        var statusUrl = $"https://grok.com/rest/media/post/{postId}";
        var deadline  = DateTime.UtcNow.AddMilliseconds(MaxWaitMs);
        var attempt   = 0;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollIntervalMs);
            attempt++;
            var elapsed = TimeSpan.FromMilliseconds(attempt * PollIntervalMs);
            onProgress?.Invoke($"⏳ {elapsed:mm\\:ss}");

            try
            {
                var response = await chrome.FetchAsync(statusUrl, "GET");
                if (response == null) continue;

                var node  = JsonNode.Parse(response);
                var state = node?["state"]?.GetValue<string>()
                         ?? node?["mediaPost"]?["state"]?.GetValue<string>()
                         ?? string.Empty;

                if (state == "FAILED") return null;

                if (state is "COMPLETE" or "SUCCEEDED" or "PUBLISHED")
                {
                    var url = node?["videoUrl"]?.GetValue<string>()
                           ?? node?["mediaPost"]?["videoUrl"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(url)) return url;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poll I2V lần {A}", attempt);
            }
        }
        return null;
    }

    private async Task<string?> UpscaleAsync(
        StealthChrome chrome, string postId, string originalUrl)
    {
        try
        {
            var payload  = new { postId, videoUrl = originalUrl };
            var response = await chrome.FetchAsync(UpscaleUrl, "POST", payload);
            if (response == null) return null;
            var node = JsonNode.Parse(response);
            return node?["videoUrl"]?.GetValue<string>()
                ?? node?["url"]?.GetValue<string>();
        }
        catch { return null; }
    }

    private async Task<string?> DownloadAsync(string videoUrl, string outputDir)
    {
        try
        {
            using var http = new System.Net.Http.HttpClient();
            var bytes      = await http.GetByteArrayAsync(videoUrl);
            var path       = Path.Combine(outputDir,
                $"grok_i2v_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            await File.WriteAllBytesAsync(path, bytes);
            _logger.LogInformation("Đã lưu: {P}", path);
            return path;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download Grok I2V lỗi");
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
