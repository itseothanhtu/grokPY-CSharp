using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace GrokPY.Services.Chrome;

/// <summary>
/// Tìm và quản lý Chrome process
/// Tương đương chrome_process_manager.py trong Python gốc
/// </summary>
public class ChromeProcessManager
{
    private readonly ILogger<ChromeProcessManager> _logger;

    public ChromeProcessManager(ILogger<ChromeProcessManager> logger)
    {
        _logger = logger;
    }

    // ─── Tìm Chrome ───────────────────────────────────────────────

    /// <summary>
    /// Tìm đường dẫn chrome.exe trên hệ thống
    /// </summary>
    public string? FindChromePath()
    {
        // 1. Kiểm tra biến môi trường
        var envPath = Environment.GetEnvironmentVariable("CHROME_EXE_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return envPath;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return FindChromeWindows();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return FindChromeMac();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return FindChromeLinux();

        return null;
    }

    private string? FindChromeWindows()
    {
        var candidates = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Google\Chrome\Application\chrome.exe")
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                _logger.LogDebug("Tìm thấy Chrome: {Path}", path);
                return path;
            }
        }

        // Thử tìm qua Registry
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe");
            var regPath = key?.GetValue(string.Empty)?.ToString();
            if (regPath != null && File.Exists(regPath))
                return regPath;
        }
        catch { }

        _logger.LogWarning("Không tìm thấy Chrome trên Windows!");
        return null;
    }

    private string? FindChromeMac()
    {
        var candidates = new[]
        {
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Applications/Google Chrome.app/Contents/MacOS/Google Chrome")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private string? FindChromeLinux()
    {
        var names = new[] { "google-chrome", "google-chrome-stable", "chromium-browser", "chromium" };
        foreach (var name in names)
        {
            try
            {
                var result = Process.Start(new ProcessStartInfo("which", name)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                var output = result?.StandardOutput.ReadToEnd().Trim();
                if (!string.IsNullOrEmpty(output) && File.Exists(output))
                    return output;
            }
            catch { }
        }
        return null;
    }

    // ─── Kill Chrome ──────────────────────────────────────────────

    /// <summary>
    /// Tắt Chrome đang dùng profileDir
    /// </summary>
    public void KillChromeByProfile(string profileDir)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try
        {
            var procs = Process.GetProcessesByName("chrome");
            foreach (var proc in procs)
            {
                try
                {
                    var cmdLine = GetProcessCommandLine(proc.Id);
                    if (cmdLine != null &&
                        cmdLine.Contains(profileDir, StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill(entireProcessTree: true);
                        _logger.LogInformation("Đã kill Chrome PID {Pid}", proc.Id);
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Lỗi kill Chrome theo profile");
        }
    }

    /// <summary>
    /// Lấy command line của process (Windows)
    /// </summary>
    private static string? GetProcessCommandLine(int pid)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {pid}");
            foreach (var obj in searcher.Get())
                return obj["CommandLine"]?.ToString();
        }
        catch { }
        return null;
    }

    // ─── Check port ───────────────────────────────────────────────

    /// <summary>
    /// Kiểm tra CDP port có đang mở không
    /// </summary>
    public static bool IsCdpReady(string host, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            client.Connect(host, port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Chờ CDP ready với timeout
    /// </summary>
    public async Task<bool> WaitForCdpAsync(string host, int port, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (IsCdpReady(host, port)) return true;
            await Task.Delay(350);
        }
        return false;
    }
}
