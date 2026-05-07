using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace GrokPY.Core.Helpers;

/// <summary>
/// Tạo Machine ID duy nhất cho từng máy tính
/// Tương đương make_machine_id() trong Python gốc
/// </summary>
public static class MachineIdHelper
{
    private const string AppSalt = "veo3_salt_v1";

    /// <summary>
    /// Tạo machine ID dạng SHA256 hex — giống hệt Python gốc
    /// </summary>
    public static string MakeMachineId()
    {
        var parts = new List<string>
        {
            RuntimeInformation.OSDescription,
            Environment.OSVersion.Version.ToString(),
            RuntimeInformation.OSArchitecture.ToString(),
            GetWindowsMachineGuid(),
            GetWindowsSystemUuid(),
            GetMacAddress()
        };

        var raw = string.Join("|", parts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().ToLower()));

        // Chuẩn hóa khoảng trắng
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ");
        raw = raw + "|" + AppSalt;

        return HmacHelper.Sha256Hex(raw);
    }

    /// <summary>
    /// Lấy MachineGuid từ Windows Registry
    /// </summary>
    private static string GetWindowsMachineGuid()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return string.Empty;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Lấy System UUID qua WMI (Windows)
    /// </summary>
    private static string GetWindowsSystemUuid()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return string.Empty;

        try
        {
            // Dùng Environment.MachineName + ProcessorCount như fallback
            // Tránh dependency WMI phức tạp
            return $"{Environment.MachineName}-{Environment.ProcessorCount}";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Lấy MAC address đầu tiên
    /// </summary>
    private static string GetMacAddress()
    {
        try
        {
            var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var nic in nics)
            {
                var mac = nic.GetPhysicalAddress().ToString();
                if (!string.IsNullOrEmpty(mac) && mac != "000000000000")
                    return mac.ToLower();
            }
        }
        catch { }
        return string.Empty;
    }
}
