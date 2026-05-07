using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrokPY.Services;
using GrokPY.Services.Chrome;
using Microsoft.Extensions.Logging;

namespace GrokPY.App.ViewModels;

/// <summary>
/// ViewModel chính — quản lý trạng thái toàn ứng dụng
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ILogger<MainViewModel> _logger;
    private readonly SettingsManager _settings;
    private readonly ChromeProcessManager _chromeManager;

    public MainViewModel(
        ILogger<MainViewModel> logger,
        SettingsManager settings,
        ChromeProcessManager chromeManager)
    {
        _logger = logger;
        _settings = settings;
        _chromeManager = chromeManager;

        // Load trạng thái ban đầu
        LoadInitialState();
    }

    // ─── Properties ───────────────────────────────────────────

    [ObservableProperty]
    private string _statusText = "Sẵn sàng";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _progressValue;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private bool _isChromeReady;

    [ObservableProperty]
    private string _accountEmail = string.Empty;

    // ─── Commands ─────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenChromeAsync()
    {
        IsBusy = true;
        StatusText = "Đang mở Chrome...";
        AppendLog("🚀 Đang khởi động Chrome...");

        try
        {
            var chromePath = _chromeManager.FindChromePath();
            if (chromePath == null)
            {
                AppendLog("❌ Không tìm thấy Chrome!");
                StatusText = "Lỗi: Không tìm thấy Chrome";
                return;
            }

            AppendLog($"✅ Chrome path: {chromePath}");
            IsChromeReady = true;
            StatusText = "Chrome đã sẵn sàng";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi mở Chrome");
            AppendLog($"❌ Lỗi: {ex.Message}");
            StatusText = "Lỗi khi mở Chrome";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ─── Helpers ──────────────────────────────────────────────

    /// <summary>
    /// Thêm dòng vào log box (thread-safe)
    /// </summary>
    public void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _logger.LogInformation(message);

        // Cập nhật UI thread
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            LogText += line + Environment.NewLine;
        });
    }

    private void LoadInitialState()
    {
        var settings = _settings.LoadSettings();
        AccountEmail = settings.Account1.Email;
    }
}
