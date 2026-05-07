using System.Security.Cryptography;
using System.Text;

namespace GrokPY.Core.Helpers;

/// <summary>
/// Helper tính HMAC-SHA256 — dùng cho license system
/// Tương đương sign_hmac_hex() trong Python gốc
/// </summary>
public static class HmacHelper
{
    /// <summary>
    /// Ký HMAC-SHA256 và trả về hex string
    /// </summary>
    public static string SignHex(string secret, string message)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var msgBytes = Encoding.UTF8.GetBytes(message);

        using var hmac = new HMACSHA256(keyBytes);
        var hash = hmac.ComputeHash(msgBytes);
        return Convert.ToHexString(hash).ToLower();
    }

    /// <summary>
    /// Tạo canonical request string để ký — giống Python gốc
    /// </summary>
    public static string CanonicalRequest(string licenseKey, string machineId, long ts, string nonce)
        => $"license_key={licenseKey}&machine_id={machineId}&ts={ts}&nonce={nonce}";

    /// <summary>
    /// Tạo canonical response string (bỏ qua features) — giống Python gốc
    /// </summary>
    public static string CanonicalResponseCore(bool ok, string licenseKey, string machineId,
        long expiresAt, long serverTs, string nonce)
    {
        var okStr = ok ? "true" : "false";
        return $"ok={okStr}&license_key={licenseKey}&machine_id={machineId}" +
               $"&expires_at={expiresAt}&server_ts={serverTs}&nonce={nonce}";
    }

    /// <summary>
    /// Hash SHA256 một chuỗi, trả về hex
    /// </summary>
    public static string Sha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLower();
    }
}
