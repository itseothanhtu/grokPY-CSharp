using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GrokPY.Core.Models;
using Microsoft.Extensions.Logging;

namespace GrokPY.Services;

/// <summary>
/// Quản lý cài đặt ứng dụng — đọc/ghi config.json
/// Tương đương settings_manager.py trong Python gốc
/// KHÁC BIỆT: Password được mã hóa DPAPI thay vì plain text
/// </summary>
public class SettingsManager
{
    private readonly ILogger<SettingsManager> _logger;
    private readonly string _configPath;
    private readonly string _dataDir;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SettingsManager(ILogger<SettingsManager> logger)
    {
        _logger = logger;

        // Lưu config tại %APPDATA%\GrokPY\
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GrokPY"
        );
        _configPath = Path.Combine(_dataDir, "config.json");

        Directory.CreateDirectory(_dataDir);
    }

    /// <summary>
    /// Thư mục data chính
    /// </summary>
    public string DataDir => _dataDir;

    /// <summary>
    /// Thư mục chrome user data mặc định
    /// </summary>
    public string ChromeUserDataRoot => Path.Combine(_dataDir, "chrome_user_data");

    /// <summary>
    /// Thư mục chrome user data cho Grok
    /// </summary>
    public string GrokChromeUserDataRoot => Path.Combine(_dataDir, "chrome_user_data_grok");

    // ─── Load / Save ───────────────────────────────────────────────

    /// <summary>
    /// Đọc AppSettings từ file. Trả về default nếu file không tồn tại.
    /// </summary>
    public AppSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                _logger.LogInformation("Config chưa tồn tại, dùng default: {Path}", _configPath);
                return new AppSettings();
            }

            var json = File.ReadAllText(_configPath, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
            _logger.LogDebug("Đã load config từ {Path}", _configPath);
            return settings ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi đọc config, dùng default");
            return new AppSettings();
        }
    }

    /// <summary>
    /// Lưu AppSettings vào file
    /// </summary>
    public void SaveSettings(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            File.WriteAllText(_configPath, json, Encoding.UTF8);
            _logger.LogDebug("Đã lưu config tại {Path}", _configPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi lưu config");
        }
    }

    // ─── Password encryption (DPAPI) ───────────────────────────────

    /// <summary>
    /// Mã hóa password bằng Windows DPAPI
    /// </summary>
    public string EncryptPassword(string plainPassword)
    {
        if (string.IsNullOrEmpty(plainPassword)) return string.Empty;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainPassword);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi mã hóa password");
            return string.Empty;
        }
    }

    /// <summary>
    /// Giải mã password từ DPAPI
    /// </summary>
    public string DecryptPassword(string encryptedBase64)
    {
        if (string.IsNullOrEmpty(encryptedBase64)) return string.Empty;
        try
        {
            var encrypted = Convert.FromBase64String(encryptedBase64);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi giải mã password");
            return string.Empty;
        }
    }

    // ─── Account helpers ───────────────────────────────────────────

    /// <summary>
    /// Lưu thông tin account (email + password mã hóa)
    /// </summary>
    public void SaveAccount(string email, string plainPassword)
    {
        var settings = LoadSettings();
        settings.Account1.Email = email;
        settings.Account1.EncryptedPassword = EncryptPassword(plainPassword);
        SaveSettings(settings);
        _logger.LogInformation("Đã lưu account: {Email}", email);
    }

    /// <summary>
    /// Lưu token sau khi login thành công
    /// </summary>
    public void SaveTokens(string sessionId, string projectId, string accessToken, string cookie)
    {
        var settings = LoadSettings();
        settings.Account1.SessionId = sessionId;
        settings.Account1.ProjectId = projectId;
        settings.Account1.AccessToken = accessToken;
        settings.Account1.Cookie = cookie;

        if (!string.IsNullOrEmpty(projectId))
            settings.Account1.UrlGenToken =
                $"https://labs.google/fx/vi/tools/flow/project/{projectId}";

        SaveSettings(settings);
        _logger.LogInformation("Đã lưu tokens. ProjectId={ProjectId}", projectId);
    }

    /// <summary>
    /// Lấy profile dir cho Chrome
    /// </summary>
    public string GetChromeProfileDir(string? profileName = null)
    {
        var name = profileName ?? LoadSettings().CurrentProfile;
        if (string.IsNullOrWhiteSpace(name)) name = "PROFILE_1";
        return Path.Combine(ChromeUserDataRoot, name);
    }

    /// <summary>
    /// Lấy profile dir cho Chrome Grok
    /// </summary>
    public string GetGrokChromeProfileDir(string? profileName = null)
    {
        var name = profileName ?? "PROFILE_1";
        return Path.Combine(GrokChromeUserDataRoot, name);
    }
}
