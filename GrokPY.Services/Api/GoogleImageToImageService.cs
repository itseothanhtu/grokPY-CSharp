using GrokPY.Core.Models;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace GrokPY.Services.Api;

/// <summary>
/// Chỉnh sửa / biến đổi ảnh dùng Google AI Sandbox (Image-to-Image)
/// Tương đương API_image_to_image.py trong Python gốc
/// Endpoint: POST https://aisandbox-pa.googleapis.com/v1/projects/{id}/flowMedia:batchEditImages
/// </summary>
public class GoogleImageToImageService : IDisposable
{
    private readonly ILogger<GoogleImageToImageService> _logger;
    private readonly SettingsManager _settings;
    private readonly HttpClient _httpClient;

    private const string BaseUrl =
        "https://aisandbox-pa.googleapis.com/v1/projects/{0}/flowMedia:batchEditImages";

    private const int MaxPollAttempts = 60;
    private const int PollIntervalMs  = 3000;

    public GoogleImageToImageService(
        ILogger<GoogleImageToImageService> logger,
        SettingsManager settings)
    {
        _logger   = logger;
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    // ─── Public API ───────────────────────────────────────────

    /// <summary>
    /// Chỉnh sửa ảnh theo prompt
    /// </summary>
    /// <param name="imagePath">Ảnh đầu vào</param>
    /// <param name="prompt">Mô tả thay đổi muốn thực hiện</param>
    /// <param name="modelKey">ImageModelKey.*</param>
    /// <param name="strength">Mức độ thay đổi 0.0-1.0 (0=giữ nguyên, 1=thay đổi hoàn toàn)</param>
    /// <param name="outputDir">Thư mục lưu ảnh</param>
    /// <param name="onProgress">Callback tiến độ</param>
    public async Task<ImageGenResult> EditAsync(
        string imagePath,
        string prompt,
        string modelKey    = ImageModelKey.Imagen4,
        float  strength    = 0.7f,
        string? outputDir  = null,
        Action<string>? onProgress = null)
    {
        if (!File.Exists(imagePath))
            return ImageGenResult.Fail($"File ảnh không tồn tại: {imagePath}");

        var cfg     = _settings.LoadSettings();
        var account = cfg.Account1;

        if (string.IsNullOrEmpty(account.AccessToken))
            return ImageGenResult.Fail("Chưa có access_token.");

        if (string.IsNullOrEmpty(account.ProjectId))
            return ImageGenResult.Fail("Chưa có projectId.");

        outputDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "GrokPY", "Images", DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(outputDir);

        try
        {
            onProgress?.Invoke("🖼️ Đang đọc ảnh...");
            var imageBytes  = await File.ReadAllBytesAsync(imagePath);
            var imageBase64 = Convert.ToBase64String(imageBytes);
            var mimeType    = GetMimeType(imagePath);

            onProgress?.Invoke("✏️ Đang gửi yêu cầu edit ảnh...");
            _logger.LogInformation("I2I: model={M}, strength={S}, file={F}",
                modelKey, strength, Path.GetFileName(imagePath));

            var url     = string.Format(BaseUrl, account.ProjectId);
            var payload = BuildPayload(imageBase64, mimeType, prompt, modelKey, strength);

            SetHeaders(account);
            var content = new StringContent(
                payload.ToJsonString(), Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                _logger.LogError("I2I lỗi {Code}: {E}", resp.StatusCode,
                    err[..Math.Min(300, err.Length)]);
                return ImageGenResult.Fail($"Server lỗi: {resp.StatusCode}");
            }

            var body   = await resp.Content.ReadAsStringAsync();
            var result = JsonNode.Parse(body);
            if (result == null) return ImageGenResult.Fail("Response không hợp lệ.");

            return await ParseAndSaveAsync(result, account, outputDir, onProgress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi I2I");
            return ImageGenResult.Fail($"Lỗi: {ex.Message}");
        }
    }

    // ─── Build Payload ────────────────────────────────────────

    private static JsonObject BuildPayload(
        string imageBase64,
        string mimeType,
        string prompt,
        string modelKey,
        float  strength)
    {
        return new JsonObject
        {
            ["requests"] = new JsonArray(
                new JsonObject
                {
                    ["imageEditConfig"] = new JsonObject
                    {
                        ["modelId"]          = modelKey,
                        ["editStrength"]     = strength,
                        ["imageCount"]       = 1
                    },
                    ["inputImage"] = new JsonObject
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

    // ─── Parse & Save ─────────────────────────────────────────

    private async Task<ImageGenResult> ParseAndSaveAsync(
        JsonNode result,
        AccountConfig account,
        string outputDir,
        Action<string>? onProgress)
    {
        var savedFiles = new List<string>();

        // Sync response — có ảnh ngay
        var images = result["responses"]?.AsArray();
        if (images != null && images.Count > 0)
        {
            foreach (var img in images)
            {
                var saved = await SaveImageAsync(img, outputDir);
                if (saved != null) savedFiles.Add(saved);
            }
            if (savedFiles.Count > 0)
            {
                onProgress?.Invoke($"✅ Edit xong {savedFiles.Count} ảnh!");
                return ImageGenResult.Success(savedFiles);
            }
        }

        // Async — poll
        var opId = result["operationId"]?.GetValue<string>()
                ?? result["name"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(opId))
        {
            onProgress?.Invoke("⏳ Đang chờ server xử lý...");
            return await PollAsync(opId, account, outputDir, onProgress);
        }

        return ImageGenResult.Fail("Response không có ảnh.");
    }

    private async Task<ImageGenResult> PollAsync(
        string operationId,
        AccountConfig account,
        string outputDir,
        Action<string>? onProgress)
    {
        var pollUrl = $"https://aisandbox-pa.googleapis.com/v1/{operationId}";

        for (int i = 0; i < MaxPollAttempts; i++)
        {
            await Task.Delay(PollIntervalMs);
            onProgress?.Invoke($"⏳ Đang xử lý... ({i + 1}/{MaxPollAttempts})");

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                req.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", account.AccessToken);
                req.Headers.TryAddWithoutValidation("Cookie", account.Cookie);

                var resp = await _httpClient.SendAsync(req);
                if (!resp.IsSuccessStatusCode) continue;

                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
                if (node == null) continue;

                var done = node["done"]?.GetValue<bool>() ?? false;
                if (!done) continue;

                var savedFiles = new List<string>();
                var responses  = node["response"]?["responses"]?.AsArray()
                              ?? node["responses"]?.AsArray();

                if (responses != null)
                    foreach (var img in responses)
                    {
                        var saved = await SaveImageAsync(img, outputDir);
                        if (saved != null) savedFiles.Add(saved);
                    }

                if (savedFiles.Count > 0)
                {
                    onProgress?.Invoke($"✅ Edit xong {savedFiles.Count} ảnh!");
                    return ImageGenResult.Success(savedFiles);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poll I2I lần {I}", i + 1);
            }
        }
        return ImageGenResult.Fail("Timeout.");
    }

    // ─── Helpers ──────────────────────────────────────────────

    private async Task<string?> SaveImageAsync(JsonNode? node, string outputDir)
    {
        if (node == null) return null;
        try
        {
            var base64 = node["imageBytes"]?.GetValue<string>()
                      ?? node["image"]?.GetValue<string>();
            if (string.IsNullOrEmpty(base64)) return null;

            var idx = base64.IndexOf(',');
            if (idx >= 0) base64 = base64[(idx + 1)..];

            var bytes    = Convert.FromBase64String(base64);
            var filePath = Path.Combine(outputDir,
                $"i2i_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            await File.WriteAllBytesAsync(filePath, bytes);
            _logger.LogInformation("Đã lưu: {P}", filePath);
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lưu ảnh I2I lỗi");
            return null;
        }
    }

    private void SetHeaders(AccountConfig account)
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", account.AccessToken);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", account.Cookie);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://labs.google");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://labs.google/");
    }

    private static string GetMimeType(string path) =>
        Path.GetExtension(path).ToLower() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp"           => "image/webp",
            _                 => "image/png"
        };

    public void Dispose() => _httpClient.Dispose();
}
