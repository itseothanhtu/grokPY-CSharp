using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GrokPY.Services.Chrome;

/// <summary>
/// Quản lý nhiều Chrome profile khác nhau
/// Cho phép chạy nhiều tài khoản Google/Grok cùng lúc
/// Tương đương phần profile management trong chrome_process_manager.py
/// </summary>
public class ChromeProfileManager
{
    private readonly ILogger<ChromeProfileManager> _logger;
    private readonly SettingsManager _settings;

    // Loại profile
    public const string TypeGoogle = "GOOGLE";  // Labs.google / Veo
    public const string TypeGrok   = "GROK";    // Grok.com
    public const string TypeSora   = "SORA";    // Sora.com

    private string ProfileIndexPath =>
        Path.Combine(_settings.DataDir, "profiles.json");

    public ChromeProfileManager(
        ILogger<ChromeProfileManager> logger,
        SettingsManager settings)
    {
        _logger  = logger;
        _settings = settings;
    }

    // ─── Profile Directory ────────────────────────────────────

    /// <summary>
    /// Lấy đường dẫn thư mục profile theo tên và loại
    /// </summary>
    public string GetProfileDir(
        string profileName = "PROFILE_1",
        string type        = TypeGoogle)
    {
        var rootDir = type switch
        {
            TypeGrok => _settings.GrokChromeUserDataRoot,
            TypeSora => Path.Combine(_settings.DataDir, "chrome_sora"),
            _        => _settings.ChromeUserDataRoot
        };

        var dir = Path.Combine(rootDir, profileName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Danh sách tất cả profile hiện có theo loại
    /// </summary>
    public List<ChromeProfile> GetAllProfiles(string type = TypeGoogle)
    {
        var rootDir = type switch
        {
            TypeGrok => _settings.GrokChromeUserDataRoot,
            TypeSora => Path.Combine(_settings.DataDir, "chrome_sora"),
            _        => _settings.ChromeUserDataRoot
        };

        if (!Directory.Exists(rootDir))
            return new List<ChromeProfile>();

        var profiles = new List<ChromeProfile>();
        foreach (var dir in Directory.GetDirectories(rootDir))
        {
            var name = Path.GetFileName(dir);
            var info = LoadProfileInfo(dir);
            profiles.Add(new ChromeProfile
            {
                Name        = name,
                Type        = type,
                Directory   = dir,
                Email       = info?.Email ?? string.Empty,
                LastUsed    = info?.LastUsed,
                HasSession  = HasValidSession(dir)
            });
        }

        return profiles.OrderBy(p => p.Name).ToList();
    }

    /// <summary>
    /// Tạo profile mới
    /// </summary>
    public ChromeProfile CreateProfile(
        string profileName,
        string type  = TypeGoogle,
        string email = "")
    {
        var dir = GetProfileDir(profileName, type);
        var profile = new ChromeProfile
        {
            Name       = profileName,
            Type       = type,
            Directory  = dir,
            Email      = email,
            LastUsed   = null,
            HasSession = false
        };

        SaveProfileInfo(dir, profile);
        _logger.LogInformation("Tạo profile: {Name} ({Type})", profileName, type);
        return profile;
    }

    /// <summary>
    /// Xoá profile (xoá cả thư mục)
    /// </summary>
    public bool DeleteProfile(string profileName, string type = TypeGoogle)
    {
        var dir = GetProfileDir(profileName, type);
        if (!Directory.Exists(dir)) return false;

        try
        {
            Directory.Delete(dir, recursive: true);
            _logger.LogInformation("Đã xoá profile: {Name}", profileName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Xoá profile lỗi: {Name}", profileName);
            return false;
        }
    }

    /// <summary>
    /// Đổi tên profile
    /// </summary>
    public bool RenameProfile(
        string oldName, string newName,
        string type = TypeGoogle)
    {
        var oldDir = GetProfileDir(oldName, type);
        var rootDir = Path.GetDirectoryName(oldDir)!;
        var newDir  = Path.Combine(rootDir, newName);

        if (!Directory.Exists(oldDir)) return false;
        if (Directory.Exists(newDir))
        {
            _logger.LogWarning("Profile {New} đã tồn tại", newName);
            return false;
        }

        try
        {
            Directory.Move(oldDir, newDir);
            _logger.LogInformation("Đổi tên: {Old} → {New}", oldName, newName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Đổi tên profile lỗi");
            return false;
        }
    }

    // ─── Session Check ────────────────────────────────────────

    /// <summary>
    /// Kiểm tra profile có session đăng nhập chưa
    /// (dựa trên sự tồn tại của cookie file)
    /// </summary>
    public bool HasValidSession(string profileDir)
    {
        // Chrome lưu cookies trong Default/Cookies (SQLite)
        var cookieFile = Path.Combine(profileDir, "Default", "Cookies");
        if (!File.Exists(cookieFile)) return false;

        // Kiểm tra file có nội dung không (> 10KB = có cookies)
        var size = new FileInfo(cookieFile).Length;
        return size > 10 * 1024;
    }

    /// <summary>
    /// Cập nhật thời gian sử dụng cuối
    /// </summary>
    public void UpdateLastUsed(string profileName, string type = TypeGoogle)
    {
        var dir = GetProfileDir(profileName, type);
        var info = LoadProfileInfo(dir) ?? new ProfileInfo();
        info.LastUsed = DateTime.UtcNow;
        SaveProfileInfo(dir, new ChromeProfile
        {
            Name     = profileName,
            Type     = type,
            Email    = info.Email,
            LastUsed = info.LastUsed
        });
    }

    /// <summary>
    /// Lưu email vào profile
    /// </summary>
    public void SaveEmail(string profileName, string email,
        string type = TypeGoogle)
    {
        var dir  = GetProfileDir(profileName, type);
        var info = LoadProfileInfo(dir) ?? new ProfileInfo();
        info.Email = email;
        SaveProfileInfo(dir, new ChromeProfile
        {
            Name     = profileName,
            Type     = type,
            Email    = info.Email,
            LastUsed = info.LastUsed
        });
    }

    // ─── Tạo tên profile tự động ─────────────────────────────

    /// <summary>
    /// Tạo tên profile tiếp theo tự động (PROFILE_1, PROFILE_2...)
    /// </summary>
    public string GetNextProfileName(string type = TypeGoogle)
    {
        var existing = GetAllProfiles(type)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i <= 100; i++)
        {
            var name = $"PROFILE_{i}";
            if (!existing.Contains(name)) return name;
        }
        return $"PROFILE_{Guid.NewGuid().ToString("N")[..6].ToUpper()}";
    }

    // ─── Disk info ────────────────────────────────────────────

    /// <summary>
    /// Tính kích thước thư mục profile (MB)
    /// </summary>
    public double GetProfileSizeMb(string profileName, string type = TypeGoogle)
    {
        var dir = GetProfileDir(profileName, type);
        if (!Directory.Exists(dir)) return 0;

        try
        {
            var bytes = Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
            return Math.Round(bytes / 1024.0 / 1024.0, 2);
        }
        catch { return 0; }
    }

    // ─── Profile Info file ────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonOpts =
        new() { WriteIndented = true };

    private static string InfoFilePath(string profileDir) =>
        Path.Combine(profileDir, "_grokpy_profile.json");

    private void SaveProfileInfo(string profileDir, ChromeProfile profile)
    {
        try
        {
            Directory.CreateDirectory(profileDir);
            var info = new ProfileInfo
            {
                Email    = profile.Email,
                Type     = profile.Type,
                LastUsed = profile.LastUsed ?? DateTime.UtcNow
            };
            var json = JsonSerializer.Serialize(info, _jsonOpts);
            File.WriteAllText(InfoFilePath(profileDir), json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lưu profile info lỗi");
        }
    }

    private ProfileInfo? LoadProfileInfo(string profileDir)
    {
        try
        {
            var path = InfoFilePath(profileDir);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ProfileInfo>(json);
        }
        catch { return null; }
    }

    private class ProfileInfo
    {
        public string   Email    { get; set; } = string.Empty;
        public string   Type     { get; set; } = TypeGoogle;
        public DateTime LastUsed { get; set; } = DateTime.UtcNow;
    }
}

// ─── Models ───────────────────────────────────────────────────

/// <summary>
/// Thông tin một Chrome profile
/// </summary>
public class ChromeProfile
{
    public string    Name       { get; set; } = string.Empty;
    public string    Type       { get; set; } = string.Empty;
    public string    Directory  { get; set; } = string.Empty;
    public string    Email      { get; set; } = string.Empty;
    public DateTime? LastUsed   { get; set; }
    public bool      HasSession { get; set; }

    public string DisplayName =>
        string.IsNullOrEmpty(Email)
            ? Name
            : $"{Name} ({Email})";

    public string StatusText =>
        HasSession ? "✅ Đã đăng nhập" : "⚪ Chưa đăng nhập";
}
