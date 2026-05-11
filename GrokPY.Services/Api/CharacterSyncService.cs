using GrokPY.Core.Models;
using GrokPY.Services.Chrome;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace GrokPY.Services.Api;

/// <summary>
/// Đồng bộ nhân vật (lip sync / face sync) dùng Google AI Sandbox
/// Tương đương API_sync_chactacter.py và A_workflow_sync_chactacter.py
/// Endpoint: POST https://aisandbox-pa.googleapis.com/v1/projects/{id}/flowMedia:batchSyncCharacter
/// </summary>
public class CharacterSyncService : IDisposable
{
    private readonly ILogger<CharacterSyncService> _logger;
    private readonly SettingsManager _settings;
    private readonly ChromeProcessManager _chromeManager;
    private readonly HttpClient _httpClient;

    private const string SyncUrl =
        "https://aisandbox-pa.googleapis.com/v1/projects/{0}/flowMedia:batchSyncCharacter";
    private const string StatusUrl =
        "https://aisandbox-pa.googleapis.com/v1/projects/{0}/flowMedia:checkSyncStatus";

    private const int MaxPollAttempts = 120;
    private const int PollIntervalMs  = 5000;

    public CharacterSyncService(
        ILogger<CharacterSyncService> logger,
        SettingsManager settings,
        ChromeProcessManager chromeManager)
    {
        _logger        = logger;
        _settings      = settings;
        _chromeManager = chromeManager;
        _httpClient    = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
    }

    // ─── Public API ───────────────────────────────────────────

    /// <summary>
    /// Sync nhân vật trong video với audio (lip sync)
    /// </summary>
    /// <param name="videoPath">Video đầu vào có nhân vật</param>
    /// <param name="audioPath">Audio muốn sync (mp3/wav)</param>
    /// <param name="outputDir">Thư mục lưu video output</param>
    /// <param name="onProgress">Callback tiến độ</param>
    public async Task<VideoGenResult> SyncAsync(
        string videoPath,
        string audioPath,
        string? outputDir  = null,
        Action<string>? onProgress = null)
    {
        if (!File.Exists(videoPath))
            return VideoGenResult.Fail($"File video không tồn tại: {videoPath}");

        if (!File.Exists(audioPath))
            return VideoGenResult.Fail($"File audio không tồn tại: {audioPath}");

        var cfg     = _settings.LoadSettings();
        var account = cfg.Account1;

        if (string.IsNullOrEmpty(account.AccessToken))
            return VideoGenResult.Fail("Chưa có access_token. Vui lòng login.");

        if (string.IsNullOrEmpty(account.ProjectId))
            return VideoGenResult.Fail("Chưa có projectId. Vui lòng login.");

        outputDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "GrokPY", "Sync", DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(outputDir);

        try
        {
            onProgress?.Invoke("🎭 Đang đọc video và audio...");
            _logger.LogInformation("CharacterSync: video={V}, audio={A}",
                Path.GetFileName(videoPath), Path.GetFileName(audioPath));

            // Đọc và encode base64
            var videoBytes  = await File.ReadAllBytesAsync(videoPath);
            var audioBytes  = await File.ReadAllBytesAsync(audioPath);
            var videoBase64 = Convert.ToBase64String(videoBytes);
            var audioBase64 = Convert.ToBase64String(audioBytes);

            onProgress?.Invoke("📤 Đang gửi yêu cầu sync...");

            // Build payload
            var payload = BuildPayload(
                videoBase64, GetVideoMime(videoPath),
                audioBase64, GetAudioMime(audioPath));

            // Gọi API
            var url = string.Format(SyncUrl, account.ProjectId);
            SetHeaders(account);

            var content = new System.Net.Http.StringContent(
                payload.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync(url, content);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync();
                _logger.LogError("Sync lỗi {Code}: {E}", resp.StatusCode,
                    err[..Math.Min(300, err.Length)]);
                return VideoGenResult.Fail($"Server lỗi: {resp.StatusCode}");
            }

            var body   = await resp.Content.ReadAsStringAsync();
            var result = JsonNode.Parse(body);
            if (result == null) return VideoGenResult.Fail("Response không hợp lệ.");

            // Lấy operationId để poll
            var operationId = result["operationId"]?.GetValue<string>()
                           ?? result["name"]?.GetValue<string>();

            if (string.IsNullOrEmpty(operationId))
            {
                // Có thể trả kết quả ngay
                var directUrl = result["videoUrl"]?.GetValue<string>()
                             ?? result["url"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(directUrl))
                {
                    var saved = await DownloadVideoAsync(directUrl, outputDir);
                    return saved != null
                        ? VideoGenResult.Success(new List<string> { saved })
                        : VideoGenResult.Fail("Download thất bại.");
                }
                return VideoGenResult.Fail("Không lấy được operationId.");
            }

            onProgress?.Invoke("⏳ Đang chờ server sync nhân vật...");
            return await PollAsync(operationId, account, outputDir, onProgress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi CharacterSync");
            return VideoGenResult.Fail($"Lỗi: {ex.Message}");
        }
    }

    /// <summary>
    /// Sync nhân vật dùng workflow (qua Chrome) — dùng khi API trực tiếp không hoạt động
    /// </summary>
    public async Task<VideoGenResult> SyncViaWorkflowAsync(
        string videoPath,
        string audioPath,
        string? profileDir = null,
        string? outputDir  = null,
        Action<string>? onProgress = null)
    {
        if (!File.Exists(videoPath))
            return VideoGenResult.Fail($"File video không tồn tại: {videoPath}");

        var chromePath = _chromeManager.FindChromePath();
        if (chromePath == null) return VideoGenResult.Fail("Không tìm thấy Chrome.");

        profileDir ??= _settings.GetChromeProfileDir();
        outputDir  ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "GrokPY", "Sync", DateTime.Now.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(outputDir);

        var chrome = new StealthChrome(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StealthChrome>.Instance);

        try
        {
            onProgress?.Invoke("🚀 Mở Chrome cho Character Sync...");
            await chrome.LaunchAsync(chromePath, profileDir);
            await Task.Delay(2000);

            var videoBytes  = await File.ReadAllBytesAsync(videoPath);
            var audioBytes  = await File.ReadAllBytesAsync(audioPath);
            var videoBase64 = Convert.ToBase64String(videoBytes);
            var audioBase64 = Convert.ToBase64String(audioBytes);

            onProgress?.Invoke("📤 Đang upload và sync qua workflow...");

            var cfg     = _settings.LoadSettings();
            var account = cfg.Account1;

            // Gọi qua JS inject trong Chrome session
            var payload = new
            {
                videoData = $"data:{GetVideoMime(videoPath)};base64,{videoBase64}",
                audioData = $"data:{GetAudioMime(audioPath)};base64,{audioBase64}",
                projectId = account.ProjectId
            };

            var syncApiUrl = string.Format(SyncUrl, account.ProjectId);
            var response   = await chrome.FetchAsync(syncApiUrl, "POST", payload);

            if (string.IsNullOrEmpty(response))
                return VideoGenResult.Fail("Không nhận được response.");

            var node = JsonNode.Parse(response);
            var opId = node?["operationId"]?.GetValue<string>()
                    ?? node?["name"]?.GetValue<string>();

            if (string.IsNullOrEmpty(opId))
                return VideoGenResult.Fail("Không lấy được operationId.");

            onProgress?.Invoke("⏳ Đang chờ sync hoàn tất...");
            return await PollAsync(opId, account, outputDir, onProgress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi SyncViaWorkflow");
            return VideoGenResult.Fail($"Lỗi: {ex.Message}");
        }
        finally
        {
            await chrome.DisposeAsync();
        }
    }

    // ─── Build Payload ────────────────────────────────────────

    private static JsonObject BuildPayload(
        string videoBase64, string videoMime,
        string audioBase64, string audioMime)
    {
        return new JsonObject
        {
            ["requests"] = new JsonArray(
                new JsonObject
                {
                    ["inputVideo"] = new JsonObject
                    {
                        ["bytesBase64Encoded"] = videoBase64,
                        ["mimeType"]           = videoMime
                    },
                    ["inputAudio"] = new JsonObject
                    {
                        ["bytesBase64Encoded"] = audioBase64,
                        ["mimeType"]           = audioMime
                    },
                    ["syncConfig"] = new JsonObject
                    {
                        ["syncType"] = "SYNC_TYPE_LIP_SYNC"
                    }
                }
            )
        };
    }

    // ─── Poll ─────────────────────────────────────────────────

    private async Task<VideoGenResult> PollAsync(
        string operationId,
        AccountConfig account,
        string outputDir,
        Action<string>? onProgress)
    {
        var pollUrl = $"https://aisandbox-pa.googleapis.com/v1/{operationId}";

        for (int i = 0; i < MaxPollAttempts; i++)
        {
            await Task.Delay(PollIntervalMs);
            var elapsed = TimeSpan.FromMilliseconds((i + 1) * PollIntervalMs);
            onProgress?.Invoke($"⏳ Sync đang xử lý... {elapsed:mm\\:ss}");

            try
            {
                var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get, pollUrl);
                req.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Bearer", account.AccessToken);
                req.Headers.TryAddWithoutValidation("Cookie", account.Cookie);

                var resp = await _httpClient.SendAsync(req);
                if (!resp.IsSuccessStatusCode) continue;

                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
                if (node == null) continue;

                var done = node["done"]?.GetValue<bool>() ?? false;
                if (!done) continue;

                // Lấy video URL từ kết quả
                var videoUrl = node["response"]?["videoUrl"]?.GetValue<string>()
                            ?? node["response"]?["url"]?.GetValue<string>()
                            ?? node["videoUrl"]?.GetValue<string>();

                if (!string.IsNullOrEmpty(videoUrl))
                {
                    var saved = await DownloadVideoAsync(videoUrl, outputDir);
                    if (saved != null)
                    {
                        onProgress?.Invoke("✅ Sync hoàn tất!");
                        return VideoGenResult.Success(new List<string> { saved });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poll sync lần {I}", i + 1);
            }
        }
        return VideoGenResult.Fail("Timeout chờ sync.");
    }

    // ─── Helpers ──────────────────────────────────────────────

    private async Task<string?> DownloadVideoAsync(string url, string outputDir)
    {
        try
        {
            var bytes    = await _httpClient.GetByteArrayAsync(url);
            var filePath = Path.Combine(outputDir,
                $"sync_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
            await File.WriteAllBytesAsync(filePath, bytes);
            _logger.LogInformation("Đã lưu sync: {P}", filePath);
            return filePath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download sync video lỗi");
            return null;
        }
    }

    private void SetHeaders(AccountConfig account)
    {
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", account.AccessToken);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "Cookie", account.Cookie);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "Origin", "https://labs.google");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
            "Referer", "https://labs.google/");
    }

    private static string GetVideoMime(string path) =>
        Path.GetExtension(path).ToLower() switch
        {
            ".mp4"  => "video/mp4",
            ".webm" => "video/webm",
            ".mov"  => "video/quicktime",
            _       => "video/mp4"
        };

    private static string GetAudioMime(string path) =>
        Path.GetExtension(path).ToLower() switch
        {
            ".mp3"  => "audio/mpeg",
            ".wav"  => "audio/wav",
            ".m4a"  => "audio/mp4",
            ".ogg"  => "audio/ogg",
            _       => "audio/mpeg"
        };

    public void Dispose() => _httpClient.Dispose();
}
