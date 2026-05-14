using GrokPY.Core.Helpers;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GrokPY.Services;

/// <summary>
/// Quản lý license của ứng dụng
/// Tương đương License.py trong Python gốc
/// Dùng HMAC-SHA256 để xác thực license key với machine ID
/// </summary>
public class LicenseManager
{
    private readonly ILogger<LicenseManager> _logger;
    private readonly SettingsManager _settings;

    // Secret key để verify HMAC — phải khớp với server
    private const string HmacSecret    = "grokpy_license_secret_v1";
    private const string LicenseServer = "https://license.grokpy.app/v1/verify";

    private string LicenseCachePath =>
        Path.Combine(_settings.DataDir, "license_state.json");

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true
    };

    public LicenseManager(
        ILogger<LicenseManager> logger,
        SettingsManager settings)
    {
        _logger   = logger;
        _settings = settings;
    }

    // ─── Public API ───────────────────────────────────────────

    /// <summary>
    /// Kiểm tra license hợp lệ không
    /// Dùng cache nếu còn hạn, không thì verify online
    /// </summary>
    public async Task<LicenseResult> CheckLicenseAsync(bool forceOnline = false)
    {
        // 1. Thử đọc cache trước
        if (!forceOnline)
        {
            var cached = LoadCachedLicense();
            if (cached != null && cached.IsValid)
            {
                _logger.LogInformation("License từ cache — hết hạn: {Exp}",
                    cached.ExpiresAt?.ToString("dd/MM/yyyy") ?? "Không giới hạn");
                return cached;
            }
        }

        // 2. Verify online
        var licenseKey = LoadLicenseKey();
        if (string.IsNullOrEmpty(licenseKey))
        {
            return LicenseResult.Invalid("Chưa nhập license key.");
        }

        return await VerifyOnlineAsync(licenseKey);
    }

    /// <summary>
    /// Lưu và verify license key mới
    /// </summary>
    public async Task<LicenseResult> ActivateAsync(string licenseKey)
    {
        if (string.IsNullOrWhiteSpace(licenseKey))
            return LicenseResult.Invalid("License key không được để trống.");

        licenseKey = licenseKey.Trim();

        // Thử verify trước khi lưu
        var result = await VerifyOnlineAsync(licenseKey);

        if (result.IsValid)
        {
            SaveLicenseKey(licenseKey);
            _logger.LogInformation("License activated: {Key}",
                MaskKey(licenseKey));
        }

        return result;
    }

    /// <summary>
    /// Xoá license key (deactivate)
    /// </summary>
    public void Deactivate()
    {
        SaveLicenseKey(string.Empty);
        if (File.Exists(LicenseCachePath))
            File.Delete(LicenseCachePath);
        _logger.LogInformation("License đã được xoá");
    }

    /// <summary>
    /// Lấy Machine ID của máy hiện tại
    /// </summary>
    public string GetMachineId() => MachineIdHelper.MakeMachineId();

    // ─── Verify Online ────────────────────────────────────────

    /// <summary>
    /// Gửi request verify lên server
    /// </summary>
    private async Task<LicenseResult> VerifyOnlineAsync(string licenseKey)
    {
        try
        {
            var machineId = GetMachineId();
            var ts        = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var nonce     = Guid.NewGuid().ToString("N")[..16];

            // Tạo HMAC signature
            var canonical = HmacHelper.CanonicalRequest(licenseKey, machineId, ts, nonce);
            var signature  = HmacHelper.SignHex(HmacSecret, canonical);

            var payload = new JsonObject
            {
                ["license_key"] = licenseKey,
                ["machine_id"]  = machineId,
                ["ts"]          = ts,
                ["nonce"]       = nonce,
                ["signature"]   = signature
            };

            using var http    = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var content       = new StringContent(
                payload.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json");

            _logger.LogDebug("Verifying license online...");
            var resp = await http.PostAsync(LicenseServer, content);
            var body = await resp.Content.ReadAsStringAsync();

            return ParseServerResponse(body, licenseKey, machineId);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Không kết nối được server license");

            // Offline fallback: dùng cache cũ dù hết hạn 7 ngày
            var cached = LoadCachedLicense(gracePeriodDays: 7);
            if (cached != null)
            {
                cached.Message = "Đang dùng chế độ offline (không có mạng)";
                return cached;
            }

            return LicenseResult.Invalid("Không thể xác thực license (không có mạng).");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi verify license");
            return LicenseResult.Invalid($"Lỗi: {ex.Message}");
        }
    }

    /// <summary>
    /// Parse response từ license server và verify HMAC
    /// </summary>
    private LicenseResult ParseServerResponse(
        string body, string licenseKey, string machineId)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node == null)
                return LicenseResult.Invalid("Response không hợp lệ.");

            var ok          = node["ok"]?.GetValue<bool>() ?? false;
            var expiresAt   = node["expires_at"]?.GetValue<long>() ?? 0;
            var serverTs    = node["server_ts"]?.GetValue<long>() ?? 0;
            var nonce       = node["nonce"]?.GetValue<string>() ?? string.Empty;
            var serverSig   = node["signature"]?.GetValue<string>() ?? string.Empty;
            var features    = node["features"]?.AsArray()
                ?.Select(f => f?.GetValue<string>() ?? string.Empty)
                .ToList() ?? new List<string>();

            // Verify HMAC từ server
            var canonical = HmacHelper.CanonicalResponseCore(
                ok, licenseKey, machineId, expiresAt, serverTs, nonce);
            var expectedSig = HmacHelper.SignHex(HmacSecret, canonical);

            if (serverSig != expectedSig)
            {
                _logger.LogWarning("HMAC signature không khớp!");
                return LicenseResult.Invalid("License response không hợp lệ (tampered).");
            }

            if (!ok)
            {
                var errMsg = node["message"]?.GetValue<string>() ?? "License không hợp lệ";
                return LicenseResult.Invalid(errMsg);
            }

            // Tạo result hợp lệ
            DateTime? expires = expiresAt > 0
                ? DateTimeOffset.FromUnixTimeSeconds(expiresAt).UtcDateTime
                : null;

            var result = LicenseResult.Valid(licenseKey, expires, features);

            // Cache lại kết quả
            SaveLicenseCache(result);

            _logger.LogInformation("License hợp lệ. Hết hạn: {Exp}",
                expires?.ToString("dd/MM/yyyy") ?? "Không giới hạn");

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Parse license response lỗi");
            return LicenseResult.Invalid($"Parse lỗi: {ex.Message}");
        }
    }

    // ─── Cache & Storage ──────────────────────────────────────

    private void SaveLicenseCache(LicenseResult result)
    {
        try
        {
            var json = JsonSerializer.Serialize(result, _jsonOpts);
            File.WriteAllText(LicenseCachePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không lưu được license cache");
        }
    }

    private LicenseResult? LoadCachedLicense(int gracePeriodDays = 0)
    {
        try
        {
            if (!File.Exists(LicenseCachePath)) return null;

            var json   = File.ReadAllText(LicenseCachePath);
            var result = JsonSerializer.Deserialize<LicenseResult>(json);
            if (result == null) return null;

            // Kiểm tra hết hạn (có grace period)
            if (result.ExpiresAt.HasValue)
            {
                var deadline = result.ExpiresAt.Value.AddDays(gracePeriodDays);
                if (DateTime.UtcNow > deadline)
                    return null;
            }

            // Cache verify hàng ngày
            if (DateTime.UtcNow - result.CachedAt > TimeSpan.FromDays(1)
                && gracePeriodDays == 0)
                return null;

            return result;
        }
        catch
        {
            return null;
        }
    }

    private string LoadLicenseKey()
    {
        try
        {
            var keyPath = Path.Combine(_settings.DataDir, "license.key");
            return File.Exists(keyPath)
                ? File.ReadAllText(keyPath).Trim()
                : string.Empty;
        }
        catch { return string.Empty; }
    }

    private void SaveLicenseKey(string key)
    {
        try
        {
            var keyPath = Path.Combine(_settings.DataDir, "license.key");
            File.WriteAllText(keyPath, key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Không lưu được license key");
        }
    }

    private static string MaskKey(string key) =>
        key.Length <= 8 ? "****" :
        key[..4] + new string('*', key.Length - 8) + key[^4..];
}

// ─── Models ───────────────────────────────────────────────────

/// <summary>
/// Kết quả kiểm tra license
/// </summary>
public class LicenseResult
{
    public bool          IsValid    { get; set; }
    public string        Message    { get; set; } = string.Empty;
    public string        LicenseKey { get; set; } = string.Empty;
    public DateTime?     ExpiresAt  { get; set; }
    public List<string>  Features   { get; set; } = new();
    public DateTime      CachedAt   { get; set; } = DateTime.UtcNow;

    public static LicenseResult Valid(
        string key, DateTime? expires, List<string> features) => new()
    {
        IsValid    = true,
        Message    = "License hợp lệ",
        LicenseKey = key,
        ExpiresAt  = expires,
        Features   = features,
        CachedAt   = DateTime.UtcNow
    };

    public static LicenseResult Invalid(string message) => new()
    {
        IsValid  = false,
        Message  = message,
        CachedAt = DateTime.UtcNow
    };

    /// <summary>Có feature cụ thể không</summary>
    public bool HasFeature(string feature) =>
        Features.Contains(feature, StringComparer.OrdinalIgnoreCase);
}
