using Microsoft.Extensions.Logging;

namespace GrokPY.Services.Workflow;

/// <summary>
/// Điều khiển trạng thái workflow: start/pause/stop/resume
/// Tương đương workflow_run_control.py trong Python gốc
/// Dùng CancellationToken + SemaphoreSlim để điều khiển luồng
/// </summary>
public class WorkflowControl
{
    private readonly ILogger<WorkflowControl> _logger;

    private CancellationTokenSource? _cts;
    private SemaphoreSlim _pauseSemaphore = new(1, 1);
    private bool _isPaused;
    private bool _isRunning;
    private readonly object _lock = new();

    public WorkflowControl(ILogger<WorkflowControl> logger)
    {
        _logger = logger;
    }

    // ─── State ────────────────────────────────────────────────

    public bool IsRunning => _isRunning;
    public bool IsPaused  => _isPaused;
    public bool IsStopped => !_isRunning;

    /// <summary>
    /// Token để cancel — pass vào các task con
    /// </summary>
    public CancellationToken Token =>
        _cts?.Token ?? CancellationToken.None;

    // ─── Control ──────────────────────────────────────────────

    /// <summary>
    /// Bắt đầu workflow — tạo CancellationToken mới
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_isRunning)
            {
                _logger.LogWarning("Workflow đang chạy, không start lại");
                return;
            }
            _cts        = new CancellationTokenSource();
            _isRunning  = true;
            _isPaused   = false;
            _pauseSemaphore = new SemaphoreSlim(1, 1);
        }
        _logger.LogInformation("▶️ Workflow STARTED");
        OnStateChanged?.Invoke(WorkflowState.Running);
    }

    /// <summary>
    /// Dừng hẳn workflow
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (!_isRunning) return;
            _cts?.Cancel();
            _isRunning = false;
            _isPaused  = false;
            // Giải phóng pause semaphore nếu đang pause
            if (_pauseSemaphore.CurrentCount == 0)
                _pauseSemaphore.Release();
        }
        _logger.LogInformation("⏹️ Workflow STOPPED");
        OnStateChanged?.Invoke(WorkflowState.Stopped);
    }

    /// <summary>
    /// Tạm dừng workflow (task đang chạy sẽ dừng ở checkpoint tiếp theo)
    /// </summary>
    public void Pause()
    {
        lock (_lock)
        {
            if (!_isRunning || _isPaused) return;
            _isPaused = true;
            // Chiếm semaphore → các task gọi CheckPauseAsync sẽ bị block
            _pauseSemaphore.Wait(0);
        }
        _logger.LogInformation("⏸️ Workflow PAUSED");
        OnStateChanged?.Invoke(WorkflowState.Paused);
    }

    /// <summary>
    /// Tiếp tục workflow sau khi pause
    /// </summary>
    public void Resume()
    {
        lock (_lock)
        {
            if (!_isPaused) return;
            _isPaused = false;
            _pauseSemaphore.Release();
        }
        _logger.LogInformation("▶️ Workflow RESUMED");
        OnStateChanged?.Invoke(WorkflowState.Running);
    }

    // ─── Checkpoint ───────────────────────────────────────────

    /// <summary>
    /// Gọi tại các điểm checkpoint trong task
    /// Sẽ block nếu đang Pause, throw nếu đã Stop
    /// </summary>
    public async Task CheckPauseAsync()
    {
        // Nếu bị cancel → throw để dừng task
        Token.ThrowIfCancellationRequested();

        // Nếu đang pause → chờ Resume
        if (_isPaused)
        {
            _logger.LogDebug("Workflow đang chờ resume...");
            await _pauseSemaphore.WaitAsync(Token);
            _pauseSemaphore.Release(); // Giải phóng cho lần sau
        }
    }

    // ─── Event ────────────────────────────────────────────────

    /// <summary>
    /// Event khi trạng thái thay đổi
    /// </summary>
    public event Action<WorkflowState>? OnStateChanged;
}

/// <summary>
/// Trạng thái workflow
/// </summary>
public enum WorkflowState
{
    Idle,
    Running,
    Paused,
    Stopped,
    Completed,
    Failed
}
