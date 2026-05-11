using GrokPY.Core.Models;
using GrokPY.Services.Chrome;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GrokPY.Services.Api;

/// <summary>
/// Tạo video từ text dùng Grok (grok.com) qua Chrome session
/// Tương đương grok_api_text_to_video.py trong Python gốc
/// KHÁC với Veo: dùng StealthChrome inject JS, không dùng HttpClient thuần
/// Endpoints:
///   POST https://grok.com/rest/media/post/create
///   POST https://grok.com/rest/app-chat/conversations/new
///   POST https://grok.com/rest/media/video/upscale
/// </summary>
public class GrokTextToVideoService
{
    private readonly ILogger<GrokTextToVideoService> _logger;
    private readonly SettingsManager _settings;
    private readonly ChromeProcessManager _chromeManager;

    private const string CreatePostUrl  = "https://grok.com/rest/media/post/create";
    private const string ChatUrl        = "https://grok.com/rest/app-chat/conversations/new";
    private const string UpscaleUrl     = "https://grok.com/rest/media/video/upscale";
    private const string GrokBaseUrl    = "https://grok.com";

    private const int MaxWaitMs      = 600_000; // 10 phút
    private const int PollIntervalMs = 3_000;

    public GrokTextToVideoService(
        ILogger<GrokTextToVideoService> logger,
        SettingsManager settings,
        ChromeProcessManager chromeManager)
    {
        _logger        = logger;
        _settings      = settings;
        _chromeManager = chromeManager;
    }

    // ─── Public API ───────────────────────────────────────────

    /// <summary>
    /// Tạo video từ text prompt dùng Grok
    /// </summary>
    public async Task<VideoGenResult> GenerateAsync(
        string prompt,
        string aspectRatio     = VideoAspectRatio.Portrait,
        bool   upscale         = true,
        string? outputDir      = null,
        string? profileDir     = null,
        Action<string>? onProgress = null)
    {
        var chromePath = _chromeManager.FindChromePath();
        if (chromePath == null)
            return VideoGenResult.Fail("Không tìm thấy Chrome.");

        profileDir ??= _settings.GetGrokChromeProfileDir();
        outputDir  ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "GrokPY", "GrokVideos",
            DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(outputDir);

        var chrome = new StealthChrome(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StealthChrome>.Instance);

        try
        {
            onProgress?.Invoke("🚀 Đang mở Chrome cho Grok...");
            await chrome.LaunchAsync(chromePath, profileDir, GrokBaseUrl, headless: false);
            await Task.Delay(2000);

            // Bước 1: Tạo media post để lấy postId
            onProgress?.Invoke("📤 Đang tạo video post...");
            var postId = await CreateMediaPostAsync(chrome, prompt, aspectRatio);
            if (string.IsNullOrEmpty(postId))
                return VideoGenResult.Fail("Không tạo được media post.");

            _logger.LogInformation("PostId: {Id}", postId);

            // Bước 2: Bắt đầu generate video qua chat conversation
            onProgress?.Invoke("🎬 Đang generate video...");
            var started = await StartVideoGenerationAsync(chrome, postId, prompt);
            if (!started)
                return VideoGenResult.Fail("Không bắt đầu được quá trình generate.");

            // Bước 3: Poll chờ video xong
            onProgress?.Invoke("⏳ Đang chờ Grok xử lý video...");
            var videoUrl = await PollForVideoUrlAsync(chrome, postId, onProgress);
            if (string.IsNullOrEmpty(videoUrl))
                return VideoGenResult.Fail("Timeout chờ video từ Grok.");

            // Bước 4: Upscale (tuỳ chọn)
            if (upscale)
            {
                onProgress?.Invoke("⬆️ Đang upscale video lên HD...");
                var upscaledUrl = await UpscaleVideoAsync(chrome, postId, videoUrl);
                if (!string.IsNullOrEmpty(upscaledUrl))
                    videoUrl = upscaledUrl;
            }

            // Bước 5: Download video
            onProgress?.Invoke("⬇️ Đang tải video về...");
            var savedPath = await DownloadVideoAsync(videoUrl, outputDir);
            if (savedPath == null)
                return VideoGenResult.Fail("Không download được video.");

            onProgress?.Invoke($"✅ Tạo video Grok thành công!");
            return VideoGenResult.Success(new List<string> { savedPath });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi tạo video Grok");
            return VideoGenResult.Fail($"Lỗi: {ex.Message}");
        }
        finally
        {
            await chrome.DisposeAsync();
        }
    }

    // ─── Step 1: Create Media Post ────────────────────────────

    private async Task<string?> CreateMediaPostAsync(
        StealthChrome chrome,
        string prompt,
        string aspectRatio)
    {
        var payload = new
        {
            mediaType      = "MEDIA_POST_TYPE_VIDEO",
            prompt         = prompt,
            aspectRatio    = aspectRatio,
            videoModel     = "grok-3"
        };

        var response = await chrome.FetchAsync(CreatePostUrl, "POST", payload);
        if (response == null) return null;

        try
        {
            var node = JsonNode.Parse(response);
            // Thử nhiều path lấy postId
            return node?["postId"]?.GetValue<string>()
                ?? node?["id"]?.GetValue<string>()
                ?? node?["mediaPost"]?["id"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Parse postId lỗi. Response: {R}",
                response[..Math.Min(200, response.Length)]);
            return null;
        }
    }

    // ─── Step 2: Start Generation ─────────────────────────────

    private async Task<bool> StartVideoGenerationAsync(
        StealthChrome chrome,
        string postId,
        string prompt)
    {
        var payload = new
        {
            message = new
            {
                content  = prompt,
                mediaPostIds = new[] { postId }
            },
            modelName = "grok-3",
            temporary = true,
            videoGen  = true
        };

        var response = await chrome.FetchAsync(ChatUrl, "POST", payload);
        if (response == null) return false;

        _logger.LogDebug("StartGen response: {R}",
            response[..Math.Min(150, response.Length)]);
        return true;
    }

    // ─── Step 3: Poll for Video URL ───────────────────────────

    /// <summary>
    /// Poll liên tục để lấy URL video khi Grok xử lý xong
    /// </summary>
    private async Task<string?> PollForVideoUrlAsync(
        StealthChrome chrome,
        string postId,
        Action<string>? onProgress)
    {
        var statusUrl = $"https://grok.com/rest/media/post/{postId}";
        var deadline  = DateTime.UtcNow.AddMilliseconds(MaxWaitMs);
        var attempt   = 0;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollIntervalMs);
            attempt++;

            var elapsed = TimeSpan.FromMilliseconds(attempt * PollIntervalMs);
            onProgress?.Invoke($"⏳ Chờ Grok xử lý... {elapsed:mm\\:ss}");

            try
            {
                var response = await chrome.FetchAsync(statusUrl, "GET");
                if (response == null) continue;

                var node = JsonNode.Parse(response);
                if (node == null) continue;

                // Kiểm tra trạng thái
                var state = node["state"]?.GetValue<string>()
                         ?? node["mediaPost"]?["state"]?.GetValue<string>()
                         ?? string.Empty;

                _logger.LogDebug("Grok video state: {S}", state);

                if (state == "FAILED")
                    return null;

                if (state is "COMPLETE" or "SUCCEEDED" or "PUBLISHED")
                {
                    // Lấy video URL
                    var url = node["videoUrl"]?.GetValue<string>()
                           ?? node["mediaPost"]?["videoUrl"]?.GetValue<string>()
                           ?? node["url"]?.GetValue<string>();

                    if (!string.IsNullOrEmpty(url))
                    {
                        _logger.LogInformation("Video URL: {U}", url[..Math.Min(80, url.Length)]);
                        return url;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poll lần {A} lỗi", attempt);
            }
        }

        return null;
    }

    // ─── Step 4: Upscale ──────────────────────────────────────

    private async Task<string?> UpscaleVideoAsync(
        StealthChrome chrome,
        string postId,
        string originalUrl)
    {
        try
        {
            var payload = new
            {
                postId   = postId,
                videoUrl = originalUrl
            };

            var response = await chrome.FetchAsync(UpscaleUrl, "POST", payload);
            if (response == null) return null;

            var node = JsonNode.Parse(response);
            return node?["videoUrl"]?.GetValue<string>()
                ?? node?["url"]?.GetValue<string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Upscale lỗi (dùng video gốc)");
            return null;
        }
    }

    // ─── Download ─────────────────────────────────────────────

    private async Task<string?> DownloadVideoAsync(string videoUrl, string outputDir)
    {
        try
        {
            using var http     = new System.Net.Http.HttpClient();
            var bytes          = await http.GetByteArrayAsync(videoUrl);
            var fileName       = $"grok_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            var filePath       = Path.Combine(outputDir, fileName);

            await File.WriteAllBytesAsync(filePath, bytes);
            _logger.LogInformation("Đã lưu: {Path} ({KB} KB)",
                filePath, bytes.Length / 1024);
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi download video Grok");
            return null;
        }
    }
}
