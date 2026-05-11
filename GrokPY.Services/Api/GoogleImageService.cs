using GrokPY.Core.Models;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GrokPY.Services.Api;

/// <summary>
/// Tạo ảnh dùng Google AI Sandbox (Imagen 4, Nano Banana...)
/// Tương đương API_Create_image.py trong Python gốc
/// Endpoint: POST https://aisandbox-pa.googleapis.com/v1/projects/{projectId}/flowMedia:batchGenerateImages
/// KHÔNG cần Chrome — dùng HttpClient thuần với access_token
/// </summary>
public class GoogleImageService : IDisposable
{
    private readonly ILogger<GoogleImageService> _logger;
    private readonly SettingsManager _settings;
    private readonly HttpClient _httpClient;

    // Endpoint tạo ảnh Google AI Sandbox
    private const string BaseUrl =
        "https://aisandbox-pa.googleapis.com/v1/projects/{0}/flowMedia:batchGenerateImages";

    // Số lần poll tối đa khi chờ kết quả
    private const int MaxPollAttempts = 60;
    private const int PollIntervalMs  = 3000;

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        WriteIndented               = true,
        PropertyNameCaseInsensitive = true
    };

    public GoogleImageService(
        ILogger<GoogleImageService> logger,
        SettingsManager settings)
    {
        _logger   = logger;
        _settings = settings;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    // ─── Public API ───────────────────────────────────────────

    /// <summary>
    /// Tạo ảnh từ text prompt
    /// </summary>
    /// <param name="prompt">Mô tả ảnh cần tạo</param>
    /// <param name="modelKey">ImageModelKey.Imagen4 | NanoBanana | ...</param>
    /// <param name="aspectRatio">ImageAspectRatio.Landscape | Portrait | Square</param>
    /// <param name="imageCount">Số lượng ảnh cần tạo (1-4)</param>
    /// <param name="seed">Seed (-1 = random)</param>
    /// <param name="outputDir">Thư mục lưu ảnh</param>
    /// <param name="onProgress">Callback báo tiến độ</param>
    public async Task<ImageGenResult> GenerateAsync(
        string prompt,
        string modelKey        = ImageModelKey.Imagen4,
        string aspectRatio     = ImageAspectRatio.Landscape,
        int    imageCount      = 1,
        int    seed            = -1,
        string? outputDir      = null,
        Action<string>? onProgress = null)
    {
        // Lấy settings
        var cfg = _settings.LoadSettings();
        var account = cfg.Account1;

        if (string.IsNullOrEmpty(account.AccessToken))
            return ImageGenResult.Fail("Chưa có access_token. Vui lòng login trước.");

        if (string.IsNullOrEmpty(account.ProjectId))
            return ImageGenResult.Fail("Chưa có projectId. Vui lòng login trước.");

        // Thư mục lưu ảnh
        outputDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "GrokPY", "Images",
            DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(outputDir);

        try
        {
            onProgress?.Invoke($"🎨 Đang tạo {imageCount} ảnh...");
            _logger.LogInformation("Tạo ảnh: model={M}, aspect={A}, count={C}",
                modelKey, aspectRatio, imageCount);

            // Xây dựng payload
            var payload = BuildPayload(prompt, modelKey, aspectRatio, imageCount, seed);

            // Gọi API
            var url      = string.Format(BaseUrl, account.ProjectId);
            var response = await PostAsync(url, payload, account.AccessToken, account.Cookie);

            if (response == null)
                return ImageGenResult.Fail("Không nhận được response từ server.");

            // Parse response — có thể là sync (có images ngay) hoặc async (có operationId)
            var result = await ParseAndDownloadAsync(
                response, outputDir, account, onProgress);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi tạo ảnh");
            return ImageGenResult.Fail($"Lỗi: {ex.Message}");
        }
    }

    // ─── Build Payload ────────────────────────────────────────

    /// <summary>
    /// Xây dựng JSON payload gửi lên Google AI Sandbox
    /// Dựa trên phân tích request từ Python gốc
    /// </summary>
    private static JsonObject BuildPayload(
        string prompt,
        string modelKey,
        string aspectRatio,
        int    imageCount,
        int    seed)
    {
        var actualSeed = seed < 0
            ? new Random().Next(1, 999999999)
            : seed;

        return new JsonObject
        {
            ["requests"] = new JsonArray(
                new JsonObject
                {
                    ["imageGenerationConfig"] = new JsonObject
                    {
                        ["modelId"]      = modelKey,
                        ["aspectRatio"]  = aspectRatio,
                        ["imageCount"]   = imageCount,
                        ["seed"]         = actualSeed
                    },
                    ["textPrompt"] = new JsonObject
                    {
                        ["text"] = prompt
                    }
                }
            )
        };
    }

    // ─── HTTP Call ────────────────────────────────────────────

    /// <summary>
    /// POST request tới Google AI Sandbox
    /// </summary>
    private async Task<JsonNode?> PostAsync(
        string url,
        JsonObject payload,
        string accessToken,
        string cookie)
    {
        var json    = payload.ToJsonString();
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        // Set headers
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", accessToken);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "Origin", "https://labs.google");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "Referer", "https://labs.google/");

        _logger.LogDebug("POST {Url}", url);
        var resp = await _httpClient.PostAsync(url, content);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogError("API lỗi {Code}: {Err}", resp.StatusCode, err[..Math.Min(200, err.Length)]);
            return null;
        }

        var body = await resp.Content.ReadAsStringAsync();
        _logger.LogDebug("Response: {Body}", body[..Math.Min(300, body.Length)]);
        return JsonNode.Parse(body);
    }

    // ─── Parse & Download ─────────────────────────────────────

    /// <summary>
    /// Parse response và download ảnh về máy
    /// </summary>
    private async Task<ImageGenResult> ParseAndDownloadAsync(
        JsonNode response,
        string outputDir,
        AccountConfig account,
        Action<string>? onProgress)
    {
        var savedFiles = new List<string>();

        // Trường hợp 1: Response trả về images ngay (sync)
        var images = response["responses"]?.AsArray();
        if (images != null && images.Count > 0)
        {
            onProgress?.Invoke("⬇️ Đang tải ảnh về...");
            foreach (var img in images)
            {
                var saved = await SaveImageFromNodeAsync(img, outputDir);
                if (saved != null) savedFiles.Add(saved);
            }

            if (savedFiles.Count > 0)
                return ImageGenResult.Success(savedFiles);
        }

        // Trường hợp 2: Response trả về operationId (async — cần poll)
        var operationId = response["operationId"]?.GetValue<string>()
                       ?? response["name"]?.GetValue<string>();

        if (!string.IsNullOrEmpty(operationId))
        {
            onProgress?.Invoke("⏳ Đang chờ Google xử lý ảnh...");
            return await PollForResultAsync(
                operationId, outputDir, account, onProgress);
        }

        _logger.LogWarning("Response không có images hoặc operationId: {R}", response.ToJsonString()[..200]);
        return ImageGenResult.Fail("Response không hợp lệ từ server.");
    }

    /// <summary>
    /// Poll liên tục cho đến khi Google xử lý xong ảnh
    /// </summary>
    private async Task<ImageGenResult> PollForResultAsync(
        string operationId,
        string outputDir,
        AccountConfig account,
        Action<string>? onProgress)
    {
        var pollUrl = $"https://aisandbox-pa.googleapis.com/v1/{operationId}";

        for (int i = 0; i < MaxPollAttempts; i++)
        {
            await Task.Delay(PollIntervalMs);
            onProgress?.Invoke($"⏳ Đang chờ... ({i + 1}/{MaxPollAttempts})");

            try
            {
                // GET để kiểm tra trạng thái
                var req = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                req.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", account.AccessToken);
                req.Headers.TryAddWithoutValidation("Cookie", account.Cookie);

                var resp = await _httpClient.SendAsync(req);
                if (!resp.IsSuccessStatusCode) continue;

                var body = await resp.Content.ReadAsStringAsync();
                var node = JsonNode.Parse(body);
                if (node == null) continue;

                // Kiểm tra done
                var done = node["done"]?.GetValue<bool>() ?? false;
                if (!done) continue;

                // Lấy ảnh từ response
                var savedFiles = new List<string>();
                var responses  = node["response"]?["responses"]?.AsArray()
                              ?? node["responses"]?.AsArray();

                if (responses != null)
                {
                    foreach (var img in responses)
                    {
                        var saved = await SaveImageFromNodeAsync(img, outputDir);
                        if (saved != null) savedFiles.Add(saved);
                    }
                }

                if (savedFiles.Count > 0)
                {
                    onProgress?.Invoke($"✅ Tạo xong {savedFiles.Count} ảnh!");
                    return ImageGenResult.Success(savedFiles);
                }

                return ImageGenResult.Fail("Xử lý xong nhưng không có ảnh.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Lỗi poll lần {I}", i + 1);
            }
        }

        return ImageGenResult.Fail("Timeout chờ Google xử lý ảnh.");
    }

    // ─── Save Image ───────────────────────────────────────────

    /// <summary>
    /// Lưu ảnh từ JSON node (base64 hoặc URL)
    /// </summary>
    private async Task<string?> SaveImageFromNodeAsync(
        JsonNode? imgNode, string outputDir)
    {
        if (imgNode == null) return null;

        try
        {
            var fileName = $"img_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png";
            var filePath = Path.Combine(outputDir, fileName);

            // Thử lấy base64 trước
            var base64 = imgNode["imageBytes"]?.GetValue<string>()
                      ?? imgNode["image"]?.GetValue<string>()
                      ?? imgNode["data"]?.GetValue<string>();

            if (!string.IsNullOrEmpty(base64))
            {
                // Xóa prefix data URI nếu có
                var idx = base64.IndexOf(',');
                if (idx >= 0) base64 = base64[(idx + 1)..];

                var bytes = Convert.FromBase64String(base64);
                await File.WriteAllBytesAsync(filePath, bytes);
                _logger.LogInformation("Đã lưu ảnh (base64): {Path}", filePath);
                return filePath;
            }

            // Thử lấy URL
            var imageUrl = imgNode["imageUri"]?.GetValue<string>()
                        ?? imgNode["url"]?.GetValue<string>();

            if (!string.IsNullOrEmpty(imageUrl))
            {
                var bytes = await _httpClient.GetByteArrayAsync(imageUrl);
                await File.WriteAllBytesAsync(filePath, bytes);
                _logger.LogInformation("Đã lưu ảnh (URL): {Path}", filePath);
                return filePath;
            }

            _logger.LogWarning("Image node không có data hoặc URL");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi lưu ảnh");
            return null;
        }
    }

    // ─── Dispose ──────────────────────────────────────────────

    public void Dispose() => _httpClient.Dispose();
}

// ─── Result Model ─────────────────────────────────────────────

/// <summary>
/// Kết quả tạo ảnh
/// </summary>
public class ImageGenResult
{
    public bool          Ok         { get; private set; }
    public string        Message    { get; private set; } = string.Empty;
    public List<string>  SavedFiles { get; private set; } = new();

    public static ImageGenResult Success(List<string> files) => new()
    {
        Ok         = true,
        Message    = $"Tạo thành công {files.Count} ảnh",
        SavedFiles = files
    };

    public static ImageGenResult Fail(string message) => new()
    {
        Ok      = false,
        Message = message
    };
}
