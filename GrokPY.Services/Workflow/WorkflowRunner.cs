using GrokPY.Services.Api;
using GrokPY.Services.Media;
using Microsoft.Extensions.Logging;

namespace GrokPY.Services.Workflow;

/// <summary>
/// Engine chạy workflow nền — xử lý danh sách prompt theo lô (batch)
/// Tương đương worker_run_workflow.py trong Python gốc
/// Hỗ trợ: Text→Video, Image→Video, Tạo ảnh, Character Sync
/// </summary>
public class WorkflowRunner
{
    private readonly ILogger<WorkflowRunner> _logger;
    private readonly WorkflowControl _control;

    // API services
    private readonly GoogleImageService _imageService;
    private readonly VeoTextToVideoService _veoT2V;
    private readonly VeoImageToVideoService _veoI2V;
    private readonly GrokTextToVideoService _grokT2V;
    private readonly GrokImageToVideoService _grokI2V;
    private readonly CharacterSyncService _charSync;
    private readonly VideoMerger _merger;

    // Thống kê
    private int _totalTasks;
    private int _completedTasks;
    private int _failedTasks;

    public WorkflowRunner(
        ILogger<WorkflowRunner> logger,
        WorkflowControl control,
        GoogleImageService imageService,
        VeoTextToVideoService veoT2V,
        VeoImageToVideoService veoI2V,
        GrokTextToVideoService grokT2V,
        GrokImageToVideoService grokI2V,
        CharacterSyncService charSync,
        VideoMerger merger)
    {
        _logger       = logger;
        _control      = control;
        _imageService = imageService;
        _veoT2V       = veoT2V;
        _veoI2V       = veoI2V;
        _grokT2V      = grokT2V;
        _grokI2V      = grokI2V;
        _charSync     = charSync;
        _merger       = merger;
    }

    // ─── Events ───────────────────────────────────────────────

    /// <summary>Báo tiến độ từng task</summary>
    public event Action<WorkflowProgress>? OnProgress;

    /// <summary>Báo khi 1 task hoàn thành</summary>
    public event Action<WorkflowTaskResult>? OnTaskCompleted;

    /// <summary>Báo khi toàn bộ workflow xong</summary>
    public event Action<WorkflowSummary>? OnCompleted;

    // ─── Public API ───────────────────────────────────────────

    /// <summary>
    /// Chạy batch Text→Video (Veo hoặc Grok)
    /// </summary>
    public async Task RunTextToVideoBatchAsync(
        List<string> prompts,
        string engine       = "veo",   // "veo" | "grok"
        string aspectRatio  = Core.Models.VideoAspectRatio.Portrait,
        int durationSeconds = 6,
        string? outputDir   = null)
    {
        _totalTasks     = prompts.Count;
        _completedTasks = 0;
        _failedTasks    = 0;

        _logger.LogInformation("▶️ T2V Batch: {N} prompts, engine={E}",
            prompts.Count, engine);

        _control.Start();

        for (int i = 0; i < prompts.Count; i++)
        {
            // Kiểm tra pause/stop
            try { await _control.CheckPauseAsync(); }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Workflow bị dừng tại task {I}", i);
                break;
            }

            var prompt = prompts[i];
            ReportProgress(i + 1, _totalTasks, $"📝 Task {i + 1}/{_totalTasks}: {Truncate(prompt)}");

            VideoGenResult result;
            try
            {
                if (engine.ToLower() == "grok")
                {
                    result = await _grokT2V.GenerateAsync(
                        prompt, aspectRatio,
                        outputDir: outputDir,
                        onProgress: msg => ReportProgress(i + 1, _totalTasks, msg));
                }
                else
                {
                    result = await _veoT2V.GenerateAsync(
                        prompt, aspectRatio, durationSeconds,
                        outputDir: outputDir,
                        onProgress: msg => ReportProgress(i + 1, _totalTasks, msg));
                }

                HandleTaskResult(prompt, result.Ok, result.Message,
                    result.Ok ? result.SavedFiles : null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Task {I} lỗi", i + 1);
                HandleTaskResult(prompt, false, ex.Message);
            }

            // Delay nhỏ giữa các task để tránh rate limit
            if (i < prompts.Count - 1)
                await Task.Delay(2000, _control.Token).ConfigureAwait(false);
        }

        FinishWorkflow();
    }

    /// <summary>
    /// Chạy batch Image→Video
    /// </summary>
    public async Task RunImageToVideoBatchAsync(
        List<ImageToVideoTask> tasks,
        string engine       = "veo",
        string aspectRatio  = Core.Models.VideoAspectRatio.Portrait,
        int durationSeconds = 6,
        string? outputDir   = null)
    {
        _totalTasks     = tasks.Count;
        _completedTasks = 0;
        _failedTasks    = 0;

        _logger.LogInformation("▶️ I2V Batch: {N} tasks, engine={E}", tasks.Count, engine);
        _control.Start();

        for (int i = 0; i < tasks.Count; i++)
        {
            try { await _control.CheckPauseAsync(); }
            catch (OperationCanceledException) { break; }

            var task = tasks[i];
            ReportProgress(i + 1, _totalTasks,
                $"🖼️ Task {i + 1}/{_totalTasks}: {Path.GetFileName(task.ImagePath)}");

            VideoGenResult result;
            try
            {
                if (engine.ToLower() == "grok")
                {
                    result = await _grokI2V.GenerateAsync(
                        task.ImagePath, task.Prompt, aspectRatio,
                        outputDir: outputDir,
                        onProgress: msg => ReportProgress(i + 1, _totalTasks, msg));
                }
                else
                {
                    result = await _veoI2V.GenerateAsync(
                        task.ImagePath, task.Prompt, aspectRatio, durationSeconds,
                        outputDir: outputDir,
                        onProgress: msg => ReportProgress(i + 1, _totalTasks, msg));
                }

                HandleTaskResult(task.ImagePath, result.Ok, result.Message,
                    result.Ok ? result.SavedFiles : null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "I2V Task {I} lỗi", i + 1);
                HandleTaskResult(task.ImagePath, false, ex.Message);
            }

            if (i < tasks.Count - 1)
                await Task.Delay(2000, _control.Token).ConfigureAwait(false);
        }

        FinishWorkflow();
    }

    /// <summary>
    /// Chạy batch tạo ảnh
    /// </summary>
    public async Task RunCreateImageBatchAsync(
        List<string> prompts,
        string modelKey    = Core.Models.ImageModelKey.Imagen4,
        string aspectRatio = Core.Models.ImageAspectRatio.Portrait,
        int imageCount     = 1,
        string? outputDir  = null)
    {
        _totalTasks     = prompts.Count;
        _completedTasks = 0;
        _failedTasks    = 0;

        _logger.LogInformation("▶️ CreateImage Batch: {N} prompts", prompts.Count);
        _control.Start();

        for (int i = 0; i < prompts.Count; i++)
        {
            try { await _control.CheckPauseAsync(); }
            catch (OperationCanceledException) { break; }

            var prompt = prompts[i];
            ReportProgress(i + 1, _totalTasks,
                $"🎨 Task {i + 1}/{_totalTasks}: {Truncate(prompt)}");

            try
            {
                var result = await _imageService.GenerateAsync(
                    prompt, modelKey, aspectRatio, imageCount,
                    outputDir: outputDir,
                    onProgress: msg => ReportProgress(i + 1, _totalTasks, msg));

                HandleTaskResult(prompt, result.Ok, result.Message,
                    result.Ok ? result.SavedFiles : null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CreateImage Task {I} lỗi", i + 1);
                HandleTaskResult(prompt, false, ex.Message);
            }

            if (i < prompts.Count - 1)
                await Task.Delay(1500, _control.Token).ConfigureAwait(false);
        }

        FinishWorkflow();
    }

    // ─── Helpers ──────────────────────────────────────────────

    private void ReportProgress(int current, int total, string message)
    {
        var pct = total > 0 ? (int)((double)current / total * 100) : 0;
        OnProgress?.Invoke(new WorkflowProgress
        {
            Current  = current,
            Total    = total,
            Percent  = pct,
            Message  = message
        });
        _logger.LogDebug("[{C}/{T}] {Msg}", current, total, message);
    }

    private void HandleTaskResult(
        string taskName, bool ok, string message,
        List<string>? outputFiles = null)
    {
        if (ok)
        {
            _completedTasks++;
            _logger.LogInformation("✅ Task OK: {Name}", Truncate(taskName));
        }
        else
        {
            _failedTasks++;
            _logger.LogWarning("❌ Task FAIL: {Name} — {Msg}", Truncate(taskName), message);
        }

        OnTaskCompleted?.Invoke(new WorkflowTaskResult
        {
            TaskName    = taskName,
            Ok          = ok,
            Message     = message,
            OutputFiles = outputFiles ?? new List<string>()
        });
    }

    private void FinishWorkflow()
    {
        _control.Stop();
        var summary = new WorkflowSummary
        {
            Total     = _totalTasks,
            Completed = _completedTasks,
            Failed    = _failedTasks
        };
        _logger.LogInformation(
            "✅ Workflow xong: {C}/{T} thành công, {F} lỗi",
            _completedTasks, _totalTasks, _failedTasks);
        OnCompleted?.Invoke(summary);
    }

    private static string Truncate(string s, int len = 40) =>
        s.Length <= len ? s : s[..len] + "...";
}

// ─── Models ───────────────────────────────────────────────────

public class ImageToVideoTask
{
    public string ImagePath { get; set; } = string.Empty;
    public string Prompt    { get; set; } = string.Empty;
}

public class WorkflowProgress
{
    public int    Current { get; set; }
    public int    Total   { get; set; }
    public int    Percent { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class WorkflowTaskResult
{
    public string       TaskName    { get; set; } = string.Empty;
    public bool         Ok          { get; set; }
    public string       Message     { get; set; } = string.Empty;
    public List<string> OutputFiles { get; set; } = new();
}

public class WorkflowSummary
{
    public int Total     { get; set; }
    public int Completed { get; set; }
    public int Failed    { get; set; }
    public string StatusText =>
        $"Hoàn thành: {Completed}/{Total} | Lỗi: {Failed}";
}
