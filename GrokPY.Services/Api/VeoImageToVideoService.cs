using GrokPY.Core.Models;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace GrokPY.Services.Api;

/// <summary>
/// Tạo video từ ảnh dùng Google Veo 3.1 (Image-to-Video)
/// Tương đương API_image_to_video.py trong Python gốc
/// Endpoint: POST https://aisandbox-pa.googleapis.com/v1/video:batchAsyncGenerateVideoImage
/// </summary>
public class VeoImageToVideoService : IDisposable
{
    private readonly ILogger<VeoImageToVideoService> _logger;
    private readonly SettingsManager _settings;
    private readonly HttpClient _httpClient;

    private const string GenerateUrl =
        "https://aisandbox-pa.googleapis.com/v1/video:batchAsyncGenerateVideoImage";
    private const string StatusUrl =
        "https://aisandbox-pa.googleapis.com/v1/video:batchCheckAsyncVideoGenerationStatus";

    private const int MaxPollAttempts = 120;
    private const int PollIntervalMs  = 5000;

    public VeoImageToVideoService(
        ILogger<VeoImageToVideoService> logger,
        SettingsManager settings)
    {
        _logger   = logger;
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
    }

    // ─── Public API ───────────────────────────────────────────

    /// <summary>
    /// Tạo video từ ảnh đầu vào
    /// </summary>
    /// <param name="imagePath">Đường dẫn file ảnh trên máy</param>
    /// <param name="prompt">Mô tả chuyển động / nội dung video</param>
    /// <param name="aspectRatio">VideoAspectRatio.Landscape | Portrait</param>
    /// <param name="durationSeconds">Độ dài video</param>
    /// <param name="outputDir">Thư mục lưu video</param>
    /// <param name="onProgress">Callback tiến độ</param>
    public async Task<VideoGenResult> GenerateAsync(
        string imagePath,
        string prompt          = "",
        string aspectRatio     = VideoAspectRatio.Portrait,
        int    durationSeconds = 6,
        string? outputDir      = null,
        Action<string>? onProgress = null)
    {
        if (!File.Exists(imagePath))
            return VideoGenResult.Fail($"File ảnh không tồn tại: {imagePath}");

        var cfg     = _settings.LoadSettings();
        var account = cfg.Account1;

        if (string.IsNullOrEmpty(account.AccessToken))
            return VideoGenResult.Fail("Chưa có access_token. Vui lòng login.");

        var modelKey = SelectModelKey(account.TypeAccount, aspectRatio);

        outputDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "GrokPY", "Videos", DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(outputDir);

        try
        {
            onProgress?.Invoke("🖼️ Đang đọc ảnh đầu vào...");

            // Đọc ảnh → base64
            var imageBytes  = await File.ReadAllBytesAsync(imagePath);
            var imageBase64 = Convert.ToBase64String(imageBytes);
            var mimeType    = GetMimeType(imagePath);

            onProgress?.Invoke("📤 Đang gửi yêu cầu Veo Image-to-Video...");
            _logger.LogInformation("Veo I2V: model={M}, image={F}",
                modelKey, Path.GetFileName(imagePath));

            // Build payload với ảnh base64
            var payload = BuildPayload(
                imageBase64, mimeType, prompt, modelKey,
                account.SessionId, durationSeconds);

            // Gọi API
            SetHeaders(account);
            var content = new StringContent(
                payload.ToJsonString(), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(GenerateUrl, content);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                _logger.LogError("Generate lỗi {Code}: {E}",
                    resp.StatusCode, err[..Math.Min(300, err.Length)]);
                return VideoGenResult.Fail($"Server lỗi: {resp.StatusCode}");
            }

            var body   = await resp.Content.ReadAsStringAsync();
            var result = JsonNode.Parse(body);
            if (result == null)
                return VideoGenResult.Fail("Response không hợp lệ.");

            // Lấy taskIds
            var taskIds = ParseTaskIds(result);
            if (taskIds.Count == 0)
                return VideoGenResult.Fail("Không lấy được taskId.");

            onProgress?.Invoke($"⏳ Server đang xử lý... ({taskIds.Count} task)");
            return await PollAndDownloadAsync(taskIds, account, outputDir, onProgress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi Veo Image-to-Video");
            return VideoGenResult.Fail($"Lỗi: {ex.Message}");
        }
    }

    // ─── Build Payload ────────────────────────────────────────

    private static JsonObject BuildPayload(
        string imageBase64,
        string mimeType,
        string prompt,
        string modelKey,
        string sessionId,
        int    durationSeconds)
    {
        return new JsonObject
        {
            ["requests"] = new JsonArray(
                new JsonObject
                {
                    ["videoGenerationConfig"] = new JsonObject
                    {
                        ["modelId"]    = modelKey,
                        ["durationMs"] = durationSeconds * 1000,
                        ["sessionId"]  = sessionId
                    },
                    ["image"] = new JsonObject
                    {
                        ["bytesBase64Encoded"] = imageBase64,
                        ["mimeType"]           = mimeType
                    },
                    ["textPrompt"] = new JsonObject
                    {
                        ["text"] = prompt
                    }
                }
            )
        };
    }

    // ─── Helpers ──────────────────────────────────────────────

    private static string SelectModelKey(string accountType, string aspectRatio)
    {
        var isPortrait = aspectRatio == VideoAspectRatio.Portrait;
        return accountType.ToUpper() switch
        {
            "ULTRA" => isPortrait ? VeoModelKey.UltraPortrait   : VeoModelKey.UltraLandscape,
            _       => isPortrait ? VeoModelKey.NormalPortrait  : VeoModelKey.NormalLandscape
        };
    }

    private static string GetMimeType(string filePath) =>
        Path.GetExtension(filePath).ToLower() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp"           => "image/webp",
            _                 => "image/png"
        };

    private void SetHeaders(AccountConfig account)
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", account.AccessToken);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", account.Cookie);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "Origin", "https://labs.google");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "Referer", "https://labs.google/");
    }

    private static List<string> ParseTaskIds(JsonNode response)
    {
        var ids       = new List<string>();
        var responses = response["responses"]?.AsArray();
        if (responses != null)
            foreach (var item in responses)
            {
                var id = item?["taskId"]?.GetValue<string>()
                      ?? item?["operationId"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
        var rootId = response["taskId"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(rootId)) ids.Add(rootId);
        return ids.Distinct().ToList();
    }

    private async Task<VideoGenResult> PollAndDownloadAsync(
        List<string> taskIds,
        AccountConfig account,
        string outputDir,
        Action<string>? onProgress)
    {
        for (int i = 0; i < MaxPollAttempts; i++)
        {
            await Task.Delay(PollIntervalMs);
            var elapsed = TimeSpan.FromMilliseconds((i + 1) * PollIntervalMs);
            onProgress?.Invoke($"⏳ Đang xử lý... {elapsed:mm\\:ss}");

            try
            {
                SetHeaders(account);
                var statusPayload = new JsonObject
                {
                    ["taskIds"] = new JsonArray(
                        taskIds.Select(id => JsonValue.Create(id)).ToArray())
                };
                var content = new StringContent(
                    statusPayload.ToJsonString(), Encoding.UTF8, "application/json");
                var resp = await _httpClient.PostAsync(StatusUrl, content);
                if (!resp.IsSuccessStatusCode) continue;

                var result = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
                if (result == null) continue;

                var savedFiles = new List<string>();
                var responses  = result["responses"]?.AsArray();
                if (responses != null)
                    foreach (var item in responses)
                    {
                        var state = item?["state"]?.GetValue<string>() ?? string.Empty;
                        if (state is not ("SUCCEEDED" or "COMPLETE")) continue;
                        var url = item?["videoUri"]?.GetValue<string>()
                               ?? item?["url"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(url)) continue;
                        var saved = await DownloadAsync(url, outputDir);
                        if (saved != null) savedFiles.Add(saved);
                    }

                if (savedFiles.Count > 0)
                {
                    onProgress?.Invoke($"✅ Tạo xong {savedFiles.Count} video!");
                    return VideoGenResult.Success(savedFiles);
                }

                var globalState = result["responses"]?[0]?["state"]?.GetValue<string>();
                if (globalState == "FAILED")
                    return VideoGenResult.Fail("Server báo FAILED.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poll I2V lần {I} lỗi", i + 1);
            }
        }
        return VideoGenResult.Fail("Timeout chờ video.");
    }

    private async Task<string?> DownloadAsync(string url, string outputDir)
    {
        try
        {
            var bytes    = await _httpClient.GetByteArrayAsync(url);
            var filePath = Path.Combine(outputDir,
                $"veo_i2v_{DateTime.Now:yyyyMMdd_HHmmss_fff}.mp4");
            await File.WriteAllBytesAsync(filePath, bytes);
            _logger.LogInformation("Đã lưu: {P} ({KB}KB)", filePath, bytes.Length / 1024);
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download lỗi");
            return null;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
