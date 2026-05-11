using GrokPY.Core.Models;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GrokPY.Services.Api;

/// <summary>
/// Tạo video từ text dùng Google Veo 3.1
/// Tương đương API_text_to_video.py trong Python gốc
/// Endpoint: POST https://aisandbox-pa.googleapis.com/v1/video:batchAsyncGenerateVideoText
/// </summary>
public class VeoTextToVideoService : IDisposable
{
    private readonly ILogger<VeoTextToVideoService> _logger;
    private readonly SettingsManager _settings;
    private readonly HttpClient _httpClient;

    private const string GenerateUrl =
        "https://aisandbox-pa.googleapis.com/v1/video:batchAsyncGenerateVideoText";
    private const string StatusUrl =
        "https://aisandbox-pa.googleapis.com/v1/video:batchCheckAsyncVideoGenerationStatus";

    private const int MaxPollAttempts = 120; // 120 * 5s = 10 phút
    private const int PollIntervalMs  = 5000;

    public VeoTextToVideoService(
        ILogger<VeoTextToVideoService> logger,
        SettingsManager settings)
    {
        _logger   = logger;
        _settings = settings;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
    }

    // ─── Public API ───────────────────────────────────────────

    /// <summary>
    /// Tạo video từ text prompt dùng Google Veo 3.1
    /// </summary>
    /// <param name="prompt">Mô tả video</param>
    /// <param name="aspectRatio">VideoAspectRatio.Landscape | Portrait</param>
    /// <param name="durationSeconds">Độ dài video (giây)</param>
    /// <param name="outputDir">Thư mục lưu video</param>
    /// <param name="onProgress">Callback tiến độ</param>
    public async Task<VideoGenResult> GenerateAsync(
        string prompt,
        string aspectRatio     = VideoAspectRatio.Portrait,
        int    durationSeconds = 6,
        string? outputDir      = null,
        Action<string>? onProgress = null)
    {
        var cfg     = _settings.LoadSettings();
        var account = cfg.Account1;

        if (string.IsNullOrEmpty(account.AccessToken))
            return VideoGenResult.Fail("Chưa có access_token. Vui lòng login trước.");

        if (string.IsNullOrEmpty(account.SessionId))
            return VideoGenResult.Fail("Chưa có sessionId. Vui lòng login trước.");

        // Chọn model key theo loại tài khoản và aspect ratio
        var modelKey = SelectModelKey(account.TypeAccount, aspectRatio);

        outputDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "GrokPY", "Videos",
            DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(outputDir);

        try
        {
            onProgress?.Invoke($"🎬 Đang gửi yêu cầu tạo video Veo 3.1...");
            _logger.LogInformation("Veo T2V: model={M}, aspect={A}", modelKey, aspectRatio);

            // Bước 1: Gửi request tạo video
            var payload    = BuildPayload(prompt, modelKey, account.SessionId, durationSeconds);
            var initResult = await PostGenerateAsync(payload, account);

            if (initResult == null)
                return VideoGenResult.Fail("Server không phản hồi.");

            // Lấy taskIds để poll
            var taskIds = ParseTaskIds(initResult);
            if (taskIds.Count == 0)
                return VideoGenResult.Fail("Không lấy được taskId từ server.");

            _logger.LogInformation("Nhận được {N} taskId(s)", taskIds.Count);
            onProgress?.Invoke($"⏳ Server đang xử lý... (taskIds: {taskIds.Count})");

            // Bước 2: Poll cho đến khi video xong
            return await PollAndDownloadAsync(taskIds, account, outputDir, onProgress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi tạo video Veo");
            return VideoGenResult.Fail($"Lỗi: {ex.Message}");
        }
    }

    // ─── Model Selection ──────────────────────────────────────

    /// <summary>
    /// Chọn model key theo loại tài khoản và aspect ratio
    /// Tương đương logic trong Python gốc
    /// </summary>
    private static string SelectModelKey(string accountType, string aspectRatio)
    {
        var isPortrait = aspectRatio == VideoAspectRatio.Portrait;

        return accountType.ToUpper() switch
        {
            "ULTRA" => isPortrait
                ? VeoModelKey.UltraPortrait
                : VeoModelKey.UltraLandscape,

            "PRO" => isPortrait
                ? VeoModelKey.NormalPortrait
                : VeoModelKey.NormalLandscape,

            _ => isPortrait // NORMAL
                ? VeoModelKey.NormalPortrait
                : VeoModelKey.NormalLandscape
        };
    }

    // ─── Build Payload ────────────────────────────────────────

    private static JsonObject BuildPayload(
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
                        ["modelId"]       = modelKey,
                        ["durationMs"]    = durationSeconds * 1000,
                        ["sessionId"]     = sessionId
                    },
                    ["textPrompt"] = new JsonObject
                    {
                        ["text"] = prompt
                    }
                }
            )
        };
    }

    // ─── HTTP ─────────────────────────────────────────────────

    private void SetHeaders(AccountConfig account)
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", account.AccessToken);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "Cookie", account.Cookie);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "Origin", "https://labs.google");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "Referer", "https://labs.google/");
    }

    private async Task<JsonNode?> PostGenerateAsync(
        JsonObject payload, AccountConfig account)
    {
        SetHeaders(account);
        var content = new StringContent(
            payload.ToJsonString(), Encoding.UTF8, "application/json");

        var resp = await _httpClient.PostAsync(GenerateUrl, content);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            _logger.LogError("Generate lỗi {Code}: {E}",
                resp.StatusCode, err[..Math.Min(300, err.Length)]);
            return null;
        }

        return JsonNode.Parse(await resp.Content.ReadAsStringAsync());
    }

    // ─── Parse TaskIds ────────────────────────────────────────

    private static List<string> ParseTaskIds(JsonNode response)
    {
        var ids = new List<string>();

        // Thử nhiều path khác nhau
        var responses = response["responses"]?.AsArray()
                     ?? response["taskIds"]?.AsArray();

        if (responses != null)
        {
            foreach (var item in responses)
            {
                var id = item?["taskId"]?.GetValue<string>()
                      ?? item?["operationId"]?.GetValue<string>()
                      ?? item?.GetValue<string>();
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
        }

        // Trường hợp response có taskId ở root
        var rootId = response["taskId"]?.GetValue<string>()
                  ?? response["operationId"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(rootId)) ids.Add(rootId);

        return ids.Distinct().ToList();
    }

    // ─── Poll & Download ──────────────────────────────────────

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
                // Gửi request kiểm tra status
                var statusPayload = new JsonObject
                {
                    ["taskIds"] = new JsonArray(
                        taskIds.Select(id => JsonValue.Create(id)).ToArray()
                    )
                };

                SetHeaders(account);
                var content = new StringContent(
                    statusPayload.ToJsonString(), Encoding.UTF8, "application/json");

                var resp = await _httpClient.PostAsync(StatusUrl, content);
                if (!resp.IsSuccessStatusCode) continue;

                var body   = await resp.Content.ReadAsStringAsync();
                var result = JsonNode.Parse(body);
                if (result == null) continue;

                // Kiểm tra có video xong không
                var savedFiles = await TryExtractAndDownloadVideosAsync(
                    result, outputDir);

                if (savedFiles.Count > 0)
                {
                    onProgress?.Invoke($"✅ Tạo xong {savedFiles.Count} video!");
                    return VideoGenResult.Success(savedFiles);
                }

                // Log trạng thái
                var state = result["responses"]?[0]?["state"]?.GetValue<string>()
                         ?? result["state"]?.GetValue<string>()
                         ?? "PROCESSING";
                _logger.LogDebug("Video state: {S}", state);

                if (state == "FAILED")
                    return VideoGenResult.Fail("Server báo FAILED.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poll lần {I} lỗi", i + 1);
            }
        }

        return VideoGenResult.Fail("Timeout — Google mất quá nhiều thời gian xử lý.");
    }

    /// <summary>
    /// Trích xuất URL video từ response và download về máy
    /// </summary>
    private async Task<List<string>> TryExtractAndDownloadVideosAsync(
        JsonNode result, string outputDir)
    {
        var savedFiles = new List<string>();

        // Tìm video URL trong response
        var responses = result["responses"]?.AsArray()
                     ?? (result.GetValueKind() == System.Text.Json.JsonValueKind.Array
                            ? result.AsArray() : null);

        if (responses == null) return savedFiles;

        foreach (var item in responses)
        {
            if (item == null) continue;

            // Kiểm tra state = SUCCEEDED
            var state = item["state"]?.GetValue<string>() ?? string.Empty;
            if (!string.IsNullOrEmpty(state) &&
                state != "SUCCEEDED" && state != "COMPLETE") continue;

            // Lấy video URL
            var videoUrl = item["videoUri"]?.GetValue<string>()
                        ?? item["url"]?.GetValue<string>()
                        ?? item["downloadUri"]?.GetValue<string>()
                        ?? item["video"]?["uri"]?.GetValue<string>();

            if (string.IsNullOrEmpty(videoUrl)) continue;

            var saved = await DownloadVideoAsync(videoUrl, outputDir);
            if (saved != null) savedFiles.Add(saved);
        }

        return savedFiles;
    }

    private async Task<string?> DownloadVideoAsync(string videoUrl, string outputDir)
    {
        try
        {
            var fileName = $"veo_{DateTime.Now:yyyyMMdd_HHmmss_fff}.mp4";
            var filePath = Path.Combine(outputDir, fileName);

            var bytes = await _httpClient.GetByteArrayAsync(videoUrl);
            await File.WriteAllBytesAsync(filePath, bytes);

            _logger.LogInformation("Đã lưu video: {Path} ({Size} KB)",
                filePath, bytes.Length / 1024);
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi download video từ {Url}", videoUrl);
            return null;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}

// ─── Result Model ─────────────────────────────────────────────

/// <summary>
/// Kết quả tạo video
/// </summary>
public class VideoGenResult
{
    public bool         Ok         { get; private set; }
    public string       Message    { get; private set; } = string.Empty;
    public List<string> SavedFiles { get; private set; } = new();

    public static VideoGenResult Success(List<string> files) => new()
    {
        Ok         = true,
        Message    = $"Tạo thành công {files.Count} video",
        SavedFiles = files
    };

    public static VideoGenResult Fail(string message) => new()
    {
        Ok      = false,
        Message = message
    };
}
