using GrokPY.Services.Api;
using GrokPY.Services.Media;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace GrokPY.Services.Workflow;

/// <summary>
/// Pipeline hoàn chỉnh: Ý tưởng → Prompt → Ảnh → Video
/// Tương đương idea_to_video.py trong Python gốc
/// Flow:
///   1. Dùng Grok AI mở rộng ý tưởng → viết prompt chi tiết
///   2. Tạo ảnh từ prompt (Google Imagen 4)
///   3. Tạo video từ ảnh (Veo 3.1 hoặc Grok)
///   4. Ghép các video lại (tuỳ chọn)
/// </summary>
public class IdeaToVideoWorkflow
{
    private readonly ILogger<IdeaToVideoWorkflow> _logger;
    private readonly WorkflowControl _control;
    private readonly SettingsManager _settings;
    private readonly GoogleImageService _imageService;
    private readonly VeoImageToVideoService _veoI2V;
    private readonly GrokImageToVideoService _grokI2V;
    private readonly VideoMerger _merger;
    private readonly HttpClient _httpClient;

    // Grok API để sinh prompt
    private const string GrokChatUrl =
        "https://grok.com/rest/app-chat/conversations/new";

    public IdeaToVideoWorkflow(
        ILogger<IdeaToVideoWorkflow> logger,
        WorkflowControl control,
        SettingsManager settings,
        GoogleImageService imageService,
        VeoImageToVideoService veoI2V,
        GrokImageToVideoService grokI2V,
        VideoMerger merger)
    {
        _logger       = logger;
        _control      = control;
        _settings     = settings;
        _imageService = imageService;
        _veoI2V       = veoI2V;
        _grokI2V      = grokI2V;
        _merger       = merger;
        _httpClient   = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    }

    // ─── Events ───────────────────────────────────────────────

    public event Action<WorkflowProgress>? OnProgress;
    public event Action<IdeaToVideoResult>? OnCompleted;

    // ─── Public API ───────────────────────────────────────────

    /// <summary>
    /// Chạy pipeline Idea → Video hoàn chỉnh
    /// </summary>
    /// <param name="idea">Ý tưởng ngắn (ví dụ: "cô gái áo dài đứng bên hồ Gươm")</param>
    /// <param name="sceneCount">Số cảnh video muốn tạo</param>
    /// <param name="videoEngine">"veo" | "grok"</param>
    /// <param name="aspectRatio">Tỷ lệ khung hình</param>
    /// <param name="outputDir">Thư mục output</param>
    /// <param name="mergeVideos">Có ghép tất cả video lại không</param>
    public async Task<IdeaToVideoResult> RunAsync(
        string idea,
        int    sceneCount   = 3,
        string videoEngine  = "veo",
        string aspectRatio  = Core.Models.VideoAspectRatio.Portrait,
        string? outputDir   = null,
        bool   mergeVideos  = true)
    {
        outputDir ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            "GrokPY", "IdeaToVideo",
            DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
        Directory.CreateDirectory(outputDir);

        var result = new IdeaToVideoResult { Idea = idea, OutputDir = outputDir };

        _control.Start();

        try
        {
            // ── Bước 1: Sinh prompts từ ý tưởng ──────────────
            Report(1, 5, $"🧠 Đang phân tích ý tưởng: {Truncate(idea)}");
            var prompts = await GeneratePromptsAsync(idea, sceneCount);

            if (prompts.Count == 0)
            {
                // Fallback: dùng chính idea làm prompt
                _logger.LogWarning("Không sinh được prompt, dùng idea gốc");
                prompts = Enumerable.Repeat(idea, sceneCount).ToList();
            }

            result.Prompts = prompts;
            _logger.LogInformation("Đã sinh {N} prompts", prompts.Count);

            // ── Bước 2: Tạo ảnh cho từng cảnh ───────────────
            Report(2, 5, $"🎨 Đang tạo {prompts.Count} ảnh...");
            var imagePaths = new List<string>();

            for (int i = 0; i < prompts.Count; i++)
            {
                try { await _control.CheckPauseAsync(); }
                catch (OperationCanceledException) { break; }

                Report(2, 5, $"🎨 Tạo ảnh {i + 1}/{prompts.Count}...");

                var imgResult = await _imageService.GenerateAsync(
                    prompts[i],
                    Core.Models.ImageModelKey.Imagen4,
                    ToImageAspectRatio(aspectRatio),
                    imageCount: 1,
                    outputDir: Path.Combine(outputDir, "images"),
                    onProgress: msg => Report(2, 5, msg));

                if (imgResult.Ok && imgResult.SavedFiles.Count > 0)
                    imagePaths.Add(imgResult.SavedFiles[0]);
                else
                    _logger.LogWarning("Tạo ảnh {I} thất bại: {Msg}", i + 1, imgResult.Message);

                await Task.Delay(1000);
            }

            result.ImagePaths = imagePaths;

            if (imagePaths.Count == 0)
                return result.Fail("Không tạo được ảnh nào.");

            // ── Bước 3: Tạo video từ mỗi ảnh ─────────────────
            Report(3, 5, $"🎬 Đang tạo {imagePaths.Count} video...");
            var videoPaths = new List<string>();

            for (int i = 0; i < imagePaths.Count; i++)
            {
                try { await _control.CheckPauseAsync(); }
                catch (OperationCanceledException) { break; }

                Report(3, 5, $"🎬 Tạo video {i + 1}/{imagePaths.Count}...");

                VideoGenResult vidResult;
                var vidOutputDir = Path.Combine(outputDir, "videos");

                if (videoEngine.ToLower() == "grok")
                {
                    vidResult = await _grokI2V.GenerateAsync(
                        imagePaths[i],
                        prompts.Count > i ? prompts[i] : idea,
                        aspectRatio,
                        outputDir: vidOutputDir,
                        onProgress: msg => Report(3, 5, msg));
                }
                else
                {
                    vidResult = await _veoI2V.GenerateAsync(
                        imagePaths[i],
                        prompts.Count > i ? prompts[i] : idea,
                        aspectRatio,
                        outputDir: vidOutputDir,
                        onProgress: msg => Report(3, 5, msg));
                }

                if (vidResult.Ok && vidResult.SavedFiles.Count > 0)
                    videoPaths.AddRange(vidResult.SavedFiles);
                else
                    _logger.LogWarning("Tạo video {I} thất bại: {Msg}",
                        i + 1, vidResult.Message);

                await Task.Delay(1000);
            }

            result.VideoPaths = videoPaths;

            if (videoPaths.Count == 0)
                return result.Fail("Không tạo được video nào.");

            // ── Bước 4: Ghép video (tuỳ chọn) ────────────────
            if (mergeVideos && videoPaths.Count > 1)
            {
                Report(4, 5, $"🔗 Đang ghép {videoPaths.Count} video...");
                var mergedPath = Path.Combine(outputDir, "final_merged.mp4");
                var mergeResult = await _merger.MergeAsync(
                    videoPaths, mergedPath,
                    onProgress: msg => Report(4, 5, msg));

                if (mergeResult.Ok)
                    result.MergedVideoPath = mergeResult.OutputPath;
            }

            // ── Bước 5: Hoàn thành ────────────────────────────
            Report(5, 5, "✅ Idea → Video hoàn tất!");
            result.Ok = true;
            result.Message = $"Tạo thành công {videoPaths.Count} video" +
                (result.MergedVideoPath != null ? " + 1 file ghép" : "");

            _logger.LogInformation("IdeaToVideo xong: {Idea} → {N} videos",
                Truncate(idea), videoPaths.Count);
        }
        catch (OperationCanceledException)
        {
            result.Message = "Workflow bị dừng bởi người dùng.";
            _logger.LogInformation("IdeaToVideo bị cancel");
        }
        catch (Exception ex)
        {
            result.Fail($"Lỗi: {ex.Message}");
            _logger.LogError(ex, "IdeaToVideo lỗi");
        }
        finally
        {
            _control.Stop();
            OnCompleted?.Invoke(result);
        }

        return result;
    }

    // ─── Sinh Prompt từ Ý tưởng ──────────────────────────────

    /// <summary>
    /// Dùng Grok AI để mở rộng ý tưởng thành nhiều prompt chi tiết
    /// Gọi qua Chrome session (có cookie Grok)
    /// </summary>
    private async Task<List<string>> GeneratePromptsAsync(string idea, int count)
    {
        try
        {
            var systemInstruction =
                $"Bạn là chuyên gia viết prompt cho AI tạo ảnh và video. " +
                $"Hãy viết {count} prompt tiếng Anh chi tiết, sinh động, mô tả {count} cảnh " +
                $"khác nhau dựa trên ý tưởng sau. Mỗi prompt trên 1 dòng riêng. " +
                $"Chỉ trả về các prompt, không giải thích thêm.";

            var userMessage = $"Ý tưởng: {idea}";

            var payload = new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["content"] = $"{systemInstruction}\n\n{userMessage}"
                },
                ["modelName"] = "grok-3",
                ["temporary"] = true
            };

            // Dùng HttpClient với cookie Grok
            var cfg     = _settings.LoadSettings();
            var account = cfg.Account1;

            if (string.IsNullOrEmpty(account.Cookie))
            {
                _logger.LogWarning("Không có Grok cookie, tự sinh prompt đơn giản");
                return SimpleExpandIdea(idea, count);
            }

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "Cookie", account.Cookie);
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "Origin", "https://grok.com");

            var content = new StringContent(
                payload.ToJsonString(), Encoding.UTF8, "application/json");
            var resp = await _httpClient.PostAsync(GrokChatUrl, content);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Grok chat lỗi {Code}", resp.StatusCode);
                return SimpleExpandIdea(idea, count);
            }

            // Parse streaming response (Grok trả về newline-delimited JSON)
            var body    = await resp.Content.ReadAsStringAsync();
            var allText = ParseGrokStreamResponse(body);

            if (string.IsNullOrWhiteSpace(allText))
                return SimpleExpandIdea(idea, count);

            // Tách thành từng prompt
            var prompts = allText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim().TrimStart('-', '*', '0', '1', '2', '3',
                    '4', '5', '6', '7', '8', '9', '.', ' '))
                .Where(p => p.Length > 15)
                .Take(count)
                .ToList();

            return prompts.Count > 0 ? prompts : SimpleExpandIdea(idea, count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sinh prompt lỗi, dùng fallback");
            return SimpleExpandIdea(idea, count);
        }
    }

    /// <summary>
    /// Parse streaming response từ Grok API
    /// </summary>
    private static string ParseGrokStreamResponse(string rawResponse)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var line in rawResponse.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var node = JsonNode.Parse(line);
                var token = node?["result"]?["response"]?["token"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(token)) sb.Append(token);
            }
            catch { }
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Fallback: tự tạo prompt đơn giản không cần Grok
    /// </summary>
    private static List<string> SimpleExpandIdea(string idea, int count)
    {
        var modifiers = new[]
        {
            "cinematic shot, golden hour lighting, photorealistic",
            "close-up portrait, soft bokeh background, dramatic lighting",
            "wide angle landscape, vibrant colors, high detail",
            "aerial view, misty atmosphere, dreamy",
            "night scene, city lights, long exposure effect"
        };

        return Enumerable.Range(0, count)
            .Select(i => $"{idea}, {modifiers[i % modifiers.Length]}")
            .ToList();
    }

    // ─── Helpers ──────────────────────────────────────────────

    private void Report(int step, int totalSteps, string message)
    {
        OnProgress?.Invoke(new WorkflowProgress
        {
            Current = step,
            Total   = totalSteps,
            Percent = (int)((double)step / totalSteps * 100),
            Message = message
        });
    }

    /// <summary>
    /// Chuyển đổi VideoAspectRatio → ImageAspectRatio
    /// </summary>
    private static string ToImageAspectRatio(string videoAspectRatio) =>
        videoAspectRatio == Core.Models.VideoAspectRatio.Landscape
            ? Core.Models.ImageAspectRatio.Landscape
            : Core.Models.ImageAspectRatio.Portrait;

    private static string Truncate(string s, int len = 40) =>
        s.Length <= len ? s : s[..len] + "...";
}

// ─── Result Model ─────────────────────────────────────────────

public class IdeaToVideoResult
{
    public bool         Ok              { get; set; }
    public string       Message         { get; set; } = string.Empty;
    public string       Idea            { get; set; } = string.Empty;
    public string       OutputDir       { get; set; } = string.Empty;
    public List<string> Prompts         { get; set; } = new();
    public List<string> ImagePaths      { get; set; } = new();
    public List<string> VideoPaths      { get; set; } = new();
    public string?      MergedVideoPath { get; set; }

    public IdeaToVideoResult Fail(string message)
    {
        Ok      = false;
        Message = message;
        return this;
    }
}
