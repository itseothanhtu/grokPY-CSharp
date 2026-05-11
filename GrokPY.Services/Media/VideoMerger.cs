using Microsoft.Extensions.Logging;

namespace GrokPY.Services.Media;

/// <summary>
/// Ghép nhiều video thành 1 video duy nhất
/// Tương đương merge+video.py trong Python gốc
/// Dùng FFmpeg (cần cài trên máy) hoặc binary concat
/// </summary>
public class VideoMerger
{
    private readonly ILogger<VideoMerger> _logger;

    public VideoMerger(ILogger<VideoMerger> logger)
    {
        _logger = logger;
    }

    // ─── Public API ───────────────────────────────────────────

    /// <summary>
    /// Ghép danh sách video theo thứ tự thành 1 file mp4
    /// </summary>
    /// <param name="videoPaths">Danh sách đường dẫn video (theo thứ tự ghép)</param>
    /// <param name="outputPath">Đường dẫn file output (.mp4)</param>
    /// <param name="onProgress">Callback tiến độ</param>
    public async Task<MergeResult> MergeAsync(
        List<string> videoPaths,
        string? outputPath = null,
        Action<string>? onProgress = null)
    {
        if (videoPaths == null || videoPaths.Count == 0)
            return MergeResult.Fail("Không có video nào để ghép.");

        if (videoPaths.Count == 1)
        {
            _logger.LogInformation("Chỉ có 1 video, không cần ghép.");
            return MergeResult.Success(videoPaths[0]);
        }

        // Kiểm tra tất cả files tồn tại
        foreach (var path in videoPaths)
            if (!File.Exists(path))
                return MergeResult.Fail($"File không tồn tại: {path}");

        // Tạo output path tự động nếu không có
        outputPath ??= Path.Combine(
            Path.GetDirectoryName(videoPaths[0])!,
            $"merged_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        _logger.LogInformation("Ghép {N} video → {Out}", videoPaths.Count, outputPath);

        try
        {
            // Thử dùng FFmpeg trước (chất lượng tốt nhất)
            var ffmpegPath = FindFfmpeg();
            if (ffmpegPath != null)
            {
                onProgress?.Invoke($"🎬 Đang ghép {videoPaths.Count} video với FFmpeg...");
                return await MergeWithFfmpegAsync(
                    ffmpegPath, videoPaths, outputPath, onProgress);
            }

            // Fallback: Binary concat (chỉ hoạt động tốt với MP4 cùng codec)
            onProgress?.Invoke($"⚠️ FFmpeg không tìm thấy, dùng binary concat...");
            _logger.LogWarning("FFmpeg không có — dùng binary concat (có thể lỗi với một số file)");
            return await BinaryConcatAsync(videoPaths, outputPath, onProgress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi ghép video");
            return MergeResult.Fail($"Lỗi: {ex.Message}");
        }
    }

    /// <summary>
    /// Ghép video từ thư mục — ghép tất cả .mp4 trong thư mục theo tên file
    /// </summary>
    public async Task<MergeResult> MergeFromDirectoryAsync(
        string directory,
        string? outputPath = null,
        string pattern     = "*.mp4",
        Action<string>? onProgress = null)
    {
        if (!Directory.Exists(directory))
            return MergeResult.Fail($"Thư mục không tồn tại: {directory}");

        // Lấy tất cả video, sắp xếp theo tên
        var videos = Directory.GetFiles(directory, pattern)
            .OrderBy(f => f)
            .ToList();

        if (videos.Count == 0)
            return MergeResult.Fail($"Không tìm thấy file {pattern} trong {directory}");

        onProgress?.Invoke($"📂 Tìm thấy {videos.Count} video trong thư mục");
        _logger.LogInformation("Ghép từ thư mục: {Dir}, {N} files", directory, videos.Count);

        outputPath ??= Path.Combine(directory, $"merged_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        return await MergeAsync(videos, outputPath, onProgress);
    }

    // ─── FFmpeg Method ────────────────────────────────────────

    /// <summary>
    /// Ghép video dùng FFmpeg — chất lượng tốt nhất
    /// </summary>
    private async Task<MergeResult> MergeWithFfmpegAsync(
        string ffmpegPath,
        List<string> videoPaths,
        string outputPath,
        Action<string>? onProgress)
    {
        // Tạo file danh sách cho FFmpeg concat demuxer
        var listFile = Path.GetTempFileName() + ".txt";

        try
        {
            // Ghi file list (FFmpeg concat format)
            var lines = videoPaths.Select(p => $"file '{p.Replace("'", "'\\''")}'");
            await File.WriteAllLinesAsync(listFile, lines);

            // Chạy FFmpeg
            // -f concat: dùng concat demuxer
            // -safe 0: cho phép absolute path
            // -c copy: copy stream không re-encode (nhanh, giữ chất lượng)
            var args = $"-y -f concat -safe 0 -i \"{listFile}\" -c copy \"{outputPath}\"";

            onProgress?.Invoke("⚙️ FFmpeg đang xử lý...");
            _logger.LogDebug("FFmpeg args: {Args}", args);

            var (exitCode, stdout, stderr) = await RunProcessAsync(ffmpegPath, args);

            if (exitCode != 0)
            {
                _logger.LogError("FFmpeg lỗi: {Err}", stderr[..Math.Min(500, stderr.Length)]);

                // Thử lại với re-encode nếu copy lỗi
                onProgress?.Invoke("🔄 Thử lại với re-encode...");
                var argsRecode = $"-y -f concat -safe 0 -i \"{listFile}\" " +
                                 $"-vcodec libx264 -acodec aac \"{outputPath}\"";
                (exitCode, _, stderr) = await RunProcessAsync(ffmpegPath, argsRecode);

                if (exitCode != 0)
                    return MergeResult.Fail($"FFmpeg thất bại: {stderr[..Math.Min(200, stderr.Length)]}");
            }

            if (!File.Exists(outputPath))
                return MergeResult.Fail("FFmpeg chạy xong nhưng không tạo được file output.");

            var sizeKb = new FileInfo(outputPath).Length / 1024;
            onProgress?.Invoke($"✅ Ghép xong! File: {Path.GetFileName(outputPath)} ({sizeKb} KB)");
            _logger.LogInformation("Ghép xong: {Out} ({KB}KB)", outputPath, sizeKb);

            return MergeResult.Success(outputPath);
        }
        finally
        {
            // Xóa file tạm
            if (File.Exists(listFile))
                File.Delete(listFile);
        }
    }

    // ─── Binary Concat Fallback ───────────────────────────────

    /// <summary>
    /// Ghép video bằng cách nối binary (fallback khi không có FFmpeg)
    /// Chỉ hoạt động với MP4 cùng codec và resolution
    /// </summary>
    private async Task<MergeResult> BinaryConcatAsync(
        List<string> videoPaths,
        string outputPath,
        Action<string>? onProgress)
    {
        try
        {
            await using var output = File.Create(outputPath);

            for (int i = 0; i < videoPaths.Count; i++)
            {
                onProgress?.Invoke(
                    $"📎 Đang ghép file {i + 1}/{videoPaths.Count}: " +
                    $"{Path.GetFileName(videoPaths[i])}");

                var bytes = await File.ReadAllBytesAsync(videoPaths[i]);
                await output.WriteAsync(bytes);

                _logger.LogDebug("Đã concat: {F} ({KB}KB)",
                    videoPaths[i], bytes.Length / 1024);
            }

            var sizeKb = new FileInfo(outputPath).Length / 1024;
            onProgress?.Invoke(
                $"✅ Đã ghép {videoPaths.Count} video → {sizeKb} KB\n" +
                $"⚠️ Lưu ý: Cài FFmpeg để có chất lượng tốt hơn.");

            _logger.LogWarning(
                "Dùng binary concat — nên cài FFmpeg để ghép chính xác hơn");
            return MergeResult.Success(outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Binary concat lỗi");
            return MergeResult.Fail($"Lỗi concat: {ex.Message}");
        }
    }

    // ─── Process Runner ───────────────────────────────────────

    /// <summary>
    /// Chạy external process async và lấy stdout/stderr
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)>
        RunProcessAsync(string exe, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new Exception("Không khởi động được process");

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        return (proc.ExitCode, stdout, stderr);
    }

    // ─── Find FFmpeg ──────────────────────────────────────────

    /// <summary>
    /// Tìm FFmpeg trên hệ thống
    /// </summary>
    private string? FindFfmpeg()
    {
        // 1. Biến môi trường
        var envPath = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath)) return envPath;

        // 2. Các vị trí thường gặp trên Windows
        var candidates = new[]
        {
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"ffmpeg\bin\ffmpeg.exe"),
            // Cùng thư mục với app
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe")
        };

        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // 3. Tìm trong PATH
        try
        {
            var result = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("where", "ffmpeg")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                });
            var output = result?.StandardOutput.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(output) && File.Exists(output)) return output;
        }
        catch { }

        _logger.LogDebug("Không tìm thấy FFmpeg");
        return null;
    }

    /// <summary>
    /// Kiểm tra FFmpeg có sẵn không — dùng để hiển thị cảnh báo trong UI
    /// </summary>
    public bool IsFfmpegAvailable() => FindFfmpeg() != null;
}

// ─── Result Model ─────────────────────────────────────────────

/// <summary>
/// Kết quả ghép video
/// </summary>
public class MergeResult
{
    public bool   Ok         { get; private set; }
    public string Message    { get; private set; } = string.Empty;
    public string OutputPath { get; private set; } = string.Empty;

    public static MergeResult Success(string path) => new()
    {
        Ok         = true,
        Message    = $"Ghép thành công: {Path.GetFileName(path)}",
        OutputPath = path
    };

    public static MergeResult Fail(string message) => new()
    {
        Ok      = false,
        Message = message
    };
}
