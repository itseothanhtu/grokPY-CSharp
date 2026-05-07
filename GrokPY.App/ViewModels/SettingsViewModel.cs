using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrokPY.Services;
using Microsoft.Extensions.Logging;

namespace GrokPY.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ILogger<SettingsViewModel> _logger;
    private readonly SettingsManager _settings;

    public SettingsViewModel(ILogger<SettingsViewModel> logger, SettingsManager settings)
    {
        _logger = logger;
        _settings = settings;
        LoadSettings();
    }

    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _accountType = "ULTRA";
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isSaved;

    public string[] AccountTypes { get; } = ["NORMAL", "PRO", "ULTRA"];

    [RelayCommand]
    private void SaveSettings()
    {
        try
        {
            var settings = _settings.LoadSettings();
            settings.Account1.Email = Email;
            settings.Account1.TypeAccount = AccountType;
            _settings.SaveSettings(settings);

            StatusMessage = "✅ Đã lưu cài đặt";
            IsSaved = true;
            _logger.LogInformation("Đã lưu settings");
        }
        catch (Exception ex)
        {
            StatusMessage = $"❌ Lỗi: {ex.Message}";
            _logger.LogError(ex, "Lỗi lưu settings");
        }
    }

    private void LoadSettings()
    {
        var settings = _settings.LoadSettings();
        Email = settings.Account1.Email;
        AccountType = settings.Account1.TypeAccount;
    }
}
