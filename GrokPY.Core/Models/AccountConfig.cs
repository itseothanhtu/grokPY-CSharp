namespace GrokPY.Core.Models;

/// <summary>
/// Cấu hình tài khoản Google / Grok
/// Tương đương account1 trong config.json của Python gốc
/// </summary>
public class AccountConfig
{
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Password đã mã hóa bằng Windows DPAPI — KHÔNG lưu plain text
    /// </summary>
    public string EncryptedPassword { get; set; } = string.Empty;

    public string SessionId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string AccessToken { get; set; } = string.Empty;
    public string Cookie { get; set; } = string.Empty;
    public string FolderUserDataGetToken { get; set; } = string.Empty;
    public string UrlGenToken { get; set; } = string.Empty;

    /// <summary>
    /// NORMAL | PRO | ULTRA
    /// </summary>
    public string TypeAccount { get; set; } = "ULTRA";
}
